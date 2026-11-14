namespace OpenSim.Optics.Layout;

/// <summary>
/// A lens body: the medium between surface <see cref="Front"/> and surface <see cref="Back"/>
/// (indices into <see cref="OpticalLayout.Surfaces"/>, Back = Front + 1). A cemented doublet is
/// two elements that share their middle surface.
/// </summary>
public readonly record struct LensElement(int Front, int Back, string Material);
