using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;

namespace OpenSim.Cfd;

/// <summary>
/// The conjugate CFD solve as the cooling of a coupled study (the heat solve each pass of
/// <see cref="OpenSim.Solvers.ElectroThermalStudy"/> runs): the air around the body is
/// resolved and the body's surface exchanges heat with it, instead of taking the environment's
/// correlation film. The environment on the thermal input supplies the ambient, the fluid and
/// gravity; the body is one body.
/// </summary>
public static class ConjugateCooling
{
    public static Func<SolveInput, CancellationToken, SolveOutput> Steady(CfdSettings cfd,
        int maxDegreeOfParallelism = -1) =>
        (input, cancellationToken) =>
        {
            var environment = input.Environment ?? throw new InvalidOperationException(
                "The CFD cooling needs the environment: the ambient temperature, the fluid and gravity.");
            if (input.TransientThermal is not null)
                throw new InvalidOperationException("The CFD cooling of a coupled study is steady.");
            return ConjugateHeatStudy.Run(input with { Environment = null }, new[] { 0 }, environment, cfd,
                maxDegreeOfParallelism: maxDegreeOfParallelism, cancellationToken: cancellationToken).Thermal;
        };
}
