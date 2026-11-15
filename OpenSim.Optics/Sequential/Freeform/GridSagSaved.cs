using OpenSim.Core.Model;

namespace OpenSim.Optics.Sequential.Freeform;

/// <summary>Converts a Grid Sag grid between its saved form (<see cref="OpticsGridSagSettings"/>)
/// and <see cref="GridSagData"/>, both in the data file's own unit (OP-25).</summary>
public static class GridSagSaved
{
    /// <summary>The grid of a saved surface; throws <see cref="ArgumentException"/> naming what
    /// is wrong.</summary>
    public static GridSagData ToData(OpticsGridSagSettings saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        int n = saved.Nx * saved.Ny;
        var noData = new bool[Math.Max(n, 0)];
        foreach (int i in saved.NoData ?? new List<int>())
        {
            if (i < 0 || i >= n)
                throw new ArgumentException($"Grid Sag data: nodata point {i} is outside the {saved.Nx} × {saved.Ny} grid.");
            noData[i] = true;
        }
        return new GridSagData(saved.Nx, saved.Ny, saved.DeltaX, saved.DeltaY, saved.UnitFlag, saved.DecenterX, saved.DecenterY,
            saved.Sag ?? new List<double>(), saved.SlopeX ?? new List<double>(), saved.SlopeY ?? new List<double>(),
            saved.Cross ?? new List<double>(), noData);
    }

    /// <summary>The saved form of a grid.</summary>
    public static OpticsGridSagSettings FromData(GridSagData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        List<int>? noData = null;
        if (data.NoData is { } nd)
            for (int i = 0; i < nd.Count; i++)
                if (nd[i]) (noData ??= new()).Add(i);
        return new OpticsGridSagSettings
        {
            Nx = data.Nx, Ny = data.Ny, DeltaX = data.DeltaX, DeltaY = data.DeltaY, UnitFlag = data.UnitFlag,
            DecenterX = data.DecenterX, DecenterY = data.DecenterY,
            Sag = data.Sag.ToList(), SlopeX = data.SlopeX.ToList(), SlopeY = data.SlopeY.ToList(), Cross = data.Cross.ToList(),
            NoData = noData,
        };
    }
}
