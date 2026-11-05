using DIR.Lib;
using Microsoft.AspNetCore.Components.Web;

namespace WebGl.Renderer;

/// <summary>
/// A keydown on a <see cref="WebGlCanvas"/>, already translated by <see cref="WebInputMapping"/>
/// into DIR.Lib's <see cref="InputKey"/> and <see cref="InputModifier"/> (the browser analogue of
/// what SDL consumers get from <c>SdlInputMapping</c>). <see cref="Original"/> is the raw Blazor
/// event, for apps that need more than the mapped key (Code, Repeat, Location, ...).
/// </summary>
public readonly record struct CanvasKeyEventArgs(InputKey Key, InputModifier Modifiers, KeyboardEventArgs Original);
