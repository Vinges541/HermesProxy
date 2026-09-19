using System;

namespace HermesProxy.World.Enums;

/// <summary>
/// Generates <c>{Enum}Names.ToStringFast(this {Enum} value)</c>, a switch from each member to its
/// name as a string literal, falling back to <c>ToString()</c> for undefined values.
/// </summary>
/// <remarks>
/// For enums named where allocation matters, such as log messages at Information and above.
/// Enum.ToString keeps an enum's name table in the runtime's reflection cache, and on .NET 10 that
/// cache does not survive a GC, gen0 included; the next ToString rebuilds every name. For
/// <see cref="Opcode"/> that is ~3,200 strings, about 280 KB, and a warning that names an opcode
/// every few seconds lands after a GC almost every time. See HermesProxy.SourceGen/EnumNameGenerator.cs.
/// </remarks>
[AttributeUsage(AttributeTargets.Enum, Inherited = false)]
internal sealed class ToStringFastAttribute : Attribute;
