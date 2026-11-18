namespace Clauderoo.Core.Tests;

/// <summary>
/// Tests that create WinForms windows run one class at a time: the tray's shared glyph images are GDI+ objects, and
/// GDI+ throws "Object is currently in use elsewhere" when two threads draw the same image at once.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WinFormsCollection
{
    public const string Name = "WinForms";
}
