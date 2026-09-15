# World/Outbox — sending now, later, or after something

The one place a handler says **"send this later"** or **"send this after that"**. A session owns two
outboxes:

- `ctx.ToClient` (`ClientOutbox`): packets to the modern client, also reached by
  `SendPacketToClient`
- `ctx.ToServer` (`ServerOutbox`): packets to the legacy server, also reached by
  `SendPacketToServer`

See the root [CLAUDE.md](../../../CLAUDE.md) for solution-wide conventions.

| File | Holds |
|---|---|
| `PacketOutbox.cs` | The engine: holds, triggers, gates, timers, lanes, release, teardown |
| `ClientOutbox.cs` | Client facade: socket routing and **parks**, `Send`, `SendOn`, `After`, `AfterBatch`; `IClientWire` |
| `ServerOutbox.cs` | Server facade: `Send`, `AfterHandled`; `IServerWire` |
| `SessionWires.cs` | Production wires: the session's realm and instance sockets; its `WorldClient` |
| `OutboxTypes.cs` | `OutboxEvent`, `OutboxGate`, `OutboxScope`, `HoldKey`, `HoldOptions`, `OutboxOptions` |

## Why this exists

Before it, every reason to delay a packet grew its own mechanism on the session:
- `_delayedPacketsToClient/ToServer`
- `PendingRealmPackets`, `PendingUninstancedPackets` + a 30 s `Thread.Sleep`
- `DeferredObjectUpdates`, `PendingPetUpdateBatches`, `PendingPetSpells`, `PendingMailListPacket`
- flag deferrals, the guild rank coalescer, and `Thread.Sleep` pacing in mail and auction

Each had its own locking (or none) and its own ordering; one drained newest-first. None was cleared as
a unit on disconnect, and none had a test. They were fed and drained from four or more threads. The
migration moves them here one slice at a time. What has moved and what hasn't is listed below.

## Map 1: send, park, hold, trigger, release

```
 handler code  (any thread today; the session owner once the executor lands)
   │  ctx.ToClient.Send(p) / SendOn(conn,p) / After(opcode,p) / AfterBatch(p)
   │  ctx.ToServer.Send(p) / AfterHandled(opcode,p)
   │  either: When(event|gate, …) / WhenAll(events, …) / Delay / Paced / Coalesce / Exclusive
   │          Cancel / Release / LaneDone
   ▼
 ┌─ SEND ── client: both sockets attached and nothing parked → straight to the socket (no lock) ─┐
 │          server: straight to the world client                                                  │
 └───────────────────────────────────────────────────────────────────────────────────────────────┘
   │ client socket not attached, or something already parked
   ▼
 ┌─ PARK (client only) ── serialize on this thread, then under the route lock ─────────┐
 │  realm not attached            → head-of-line queue                                 │
 │  instance not requested yet    → early-instance queue (realm packets keep flowing)  │
 │  instance connecting           → head-of-line queue (everything waits, in order)    │
 │  Attach(conn) → one drainer writes them out; later sends queue behind it            │
 └──────────────────────────────────────────────────────────────────────────────────────┘

   │ hold
   ▼
 ┌─ under the outbox lock ─────────────────────────────────────────────────────┐
 │  register: event lists · gate lists · key index · lanes · deadline          │
 │  trigger:  move every hold that is now satisfied onto a local release list  │
 └──────────────────────────────────┬──────────────────────────────────────────┘
                                    │ lock released. Nothing below runs under it
                                    ▼
        release in registration order: send the packet (it may park), or run the continuation
        (a release that triggers more releases appends to the same run, breadth first,
         and the thread that fired the trigger finishes the whole run before returning)

 triggers  OpcodeSent ............ automatic when a client packet reaches its socket (incl. drains)
           OpcodeHandled ......... GlobalSessionData.OnLegacyPacketHandled, after every SMSG handler
           Signal(UpdateBatchEnd)  same place, after SMSG_(COMPRESSED_)UPDATE_OBJECT
           Gate InWorld .......... CharacterHandler: opened on SMSG_LOGIN_VERIFY_WORLD,
                                   closed on login failure and ReplaceGameState
           Tick() / TickParks() .. same place (legacy receive thread) · timer thread for timer-safe
           Attach / Detach ....... WorldSocket.HandleEnterEncryptedModeAck; LOG_DISCONNECT; closed-socket send;
                                   logout; OnDisconnect · BeginInstanceConnect: CharacterSystem login
 teardown  Discard(GameState) + DiscardParked + gates closed ← GlobalSessionData.ReplaceGameState
           Discard(LegacyConnection) ← WorldClient.Disconnect
           Discard(Session)          ← GlobalSessionData.OnDisconnect (also closes gates, frees lanes)
```

## Map 2: where the session is heading

```
                 Before B          After B (now)                   After C          After D-E
 hold-backs      ~20 bespoke       transport + sleeps moved;       outbox only      outbox only
                                   data holds still bespoke
 threads         4+                4+                              4+               1 owner at a time
 locks           4 session         ObjectCache + DeferredUpdates   ObjectCache      none on session state
                                   + outbox + route
 handler sleeps  mail, auction,    none                            none             none; connect spin gone
                 30 s instance
```

**Moved in slice B:**
- `PendingRealmPackets` and `PendingUninstancedPackets` + the 30 s sleep → parks
- `_delayedPacketsToServer` (name queries) → `When(InWorld)`
- `_delayedPacketsToClient`: spell history → `After(SMSG_SEND_UNLEARN_SPELLS)`; collision height → `AfterBatch`
- guild rank coalescer → `Coalesce`
- vanilla mail sleep → `Paced`
- auction split sleeps → `PartialStackAuctionPost` on `Exclusive` + `When(UpdateBatchEnd)`
- direct `RealmSocket.SendX()` calls in legacy handlers → `WorldSocket.BuildX` + `SendPacketToClient`

**Still bespoke (slice C):** `DeferredObjectUpdates`, `PendingPetUpdateBatches`, `PendingPetSpells`,
`PendingMailListPacket`, `DeferredCorpseDestroys`, `PendingToysSync`, `DeferredAttackStop`.

## Which call for which situation

| Situation | Call | Example |
|---|---|---|
| Send now | `SendPacketToClient(p)` / `SendPacketToServer(p)` (or `ToClient.Send` / `ToServer.Send`) | almost everything |
| Reply on a named socket, whatever the packet's type says | `ToClient.SendOn(conn, p)` | |
| Send right after the client got packet X | `ToClient.After(opcode, p)` | spell history after `SMSG_SEND_UNLEARN_SPELLS` |
| Send after the legacy update batch being handled is fully out | `ToClient.AfterBatch(p)` | collision height after the mount Values |
| Send once the legacy server's packet X has been handled | `ToServer.AfterHandled(opcode, p)` | |
| Wait for a state | `When(OutboxGate.X, p)` plus `SetGate(X, true)` where the state changes | name queries until in world |
| Wait for several pieces of data | `WhenAll([ItemTemplate(a), ItemTemplate(b)], build)` plus `Notify` as each arrives | (slice C) player create waiting for item templates |
| Space packets apart | `Paced(key, interval, p)` | multi-attachment mail on vanilla |
| Collapse a burst to the newest | `Coalesce(key, window, build, new(RunOnTimer: true))` | guild rank permissions |
| Run multi-packet work one at a time | `Exclusive(lane, start)` … `LaneDone(lane)` on every exit path | partial-stack auction posts |
| Wait for a condition checked after each update | register `When(Signal(UpdateBatchEnd), check, new(Key: k))`, **then** check once and `Cancel(k)` if already true | `PartialStackAuctionPost.Arm` |
| Undo a hold | `Cancel(key)` | a corpse destroy undone by a recreate |
| Force a hold out early | `Release(key)` | |

Keys need a `HoldKeyKind`. Add one per feature to `OutboxTypes.cs`, so two features can't collide on
the same number.

## Guarantees

- **Order.**
  - Holds released by one trigger go out in registration order.
  - A packet written by `Send` goes out before anything its trigger releases.
  - Parked packets drain in the order they parked. A packet sent during a drain queues behind it.
  - While the instance connection is being made, realm and instance packets share one queue, so neither
    overtakes the other.
- **No send pump, no per-socket queue.** On the fast path a thread's own sends reach the wire in the
  order it made them, on both connections. ad0122ee broke exactly this.
- **Next occurrence.** An event releases holds registered **before** it fired. A hold registered during a
  release waits for the next trigger.
- **Gates are state.** A hold on an open gate goes out immediately. The check and the hold are one step.
- **Bounded.**
  - **Hold timeouts:** 60 s by default, then dropped.
  - **Head-of-line parks:** dropped after `ClientOutbox.ParkTimeout` (30 s). Early-instance parks
    don't expire, because the player may sit at character select.
  - **Caps:** 4096 holds and 8192 parked packets.
  - **Lanes:** a lane waiter that times out is dropped, never started.
- **Contained.** A throwing continuation, wire write or timer callback is logged and dropped.
- **Late data is harmless.** An event after its hold timed out, was cancelled or was discarded does
  nothing.
- **Nothing leaks.** Dropped packets are disposed.

## Threading rules

- **Any thread may call anything.**
  - Hold state lives under the outbox lock; park state under the client route lock.
  - The two locks are never held together.
  - Wire writes, continuations and disposal never run under either.
- **Parked packets are serialized on the thread that parks them.** `UpdateObject.Write` reads and
  mutates session state, and the thread that drains a park is a socket thread or the pool.
- **Held client packets are serialized when released,** on the releasing thread.
- **Deadlines and the timer thread.** A deadline only acts on the timer thread when acting reads no
  session state:
  - a legacy packet;
  - a drop;
  - a hold marked `RunOnTimer`.

  **Everything else waits for `Tick()`,** which `OnLegacyPacketHandled` calls after every legacy packet.
  A deadline-driven client or continuation release therefore needs the legacy server to be sending,
  which it constantly is while in the world.
- **Drains** hand the rest of a long backlog to the thread pool after 8 batches of 64, so a socket
  thread's next read isn't held up by the login burst.

## Anti-patterns

- **A new `Pending*` / `Deferred*` / `Delayed*` field on the session.** Add an event, gate, key or lane
  here.
- **`Thread.Sleep` in a handler to space packets or wait for the other thread.** Use `Paced`, `Delay`, or
  a continuation on the event you were waiting for.
- **Writing to `session.RealmSocket` / `InstanceSocket` directly from a legacy handler.** It throws while
  the socket is absent and skips the parks. Build the packet and `SendPacketToClient` it.
- **Checking a condition, then registering a hold for it.** The update can land in between. Register
  first, then check, and `Cancel` the hold if the check already passed.
- **A per-socket send queue or send pump.** ad0122ee gave each modern socket its own channel. SpellPrepare
  (realm) and SpellStart/SpellGo (instance), written back to back, then reached the wire in either
  order, and action-bar highlights stuck. It was reverted in d3f61b9b.
- **Queueing a legacy send while the crypt is switched on outside that queue.** The uncommitted
  "Wave 2-C" loop let `InitializeEncryption` run before `CMSG_AUTH_SESSION` reached the socket.
- **Logging a hold, park or serialization to the `.pkt` sniff.** The record belongs where wire order is
  set, in `WorldSocket.SendPacket` / `WorldClient.SendPacket`. Captures from before 9cfe3c74 didn't
  follow this, so treat ordering conclusions drawn from them as unverified.
- **A hold whose timeout sends a stale packet by default.** Choose `OnTimeout: Release` only when late is
  better than never.

## Logging and tests

- **Logging.** `World/Logging/OutboxLogMessages.cs`, EventId 1500-1519.
  - **Held, released, parked, park discarded:** Debug.
  - **A timeout:** Warning at most once per 10 s per outbox.
  - **Parks dropped after waiting:** Warning.
  - **Callout failures, overflow, park overflow, runaway chains:** Error.
- **Tests.**
  - **`HermesProxy.Tests/World/Outbox`:** the recording wires record each write with its thread and
    can simulate a closed socket; `FakeTimeProvider` drives deadlines.
  - **Pins:**
    - `Send_NoHolds_WritesInCallOrderAcrossBothConnections` and
      `InstanceConnecting_EverythingWaitsInOneQueue_SoNothingOvertakes` (ad0122ee);
    - `PacketSentDuringADrain_QueuesBehindIt`;
    - `ParkedPacket_IsSerializedOnTheThreadThatParksIt`;
    - `Gate_CheckAndHold_IsAtomicAgainstAConcurrentOpen`.
  - **The auction sequence:** `World/Server/PartialStackAuctionPostTests.cs`.

## Adding a trigger

1. Add the event kind (or signal, gate, key kind) to `OutboxTypes.cs`, with a factory on `OutboxEvent`.
2. Call `Notify(...)` or `SetGate(...)` at the one place the fact becomes true, and say in a comment why
   that is the place.
3. Test it through `ClientOutbox` or `ServerOutbox` with the recording wires: registration before and
   after the event, timeout, and discard.
4. Add a row to the table above.
