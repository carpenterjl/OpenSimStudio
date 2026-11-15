% OP-98 check: JGraph drives OpenSim's optics model in-process through NET.addAssembly.
% A fused-silica singlet is built from OpenSim's own types, given the default merit function
% plus a focal-length row, optimised by the local optimizer, and its FFT MTF is plotted.
% OpenSim.Tests/Cli/ScriptingSurfaceTests.cs makes the same calls directly and pins every
% type and member named here.
bin = 'D:\EE Projects\OpenSimStudio\OpenSim.Cli\bin\Debug\net8.0\';
NET.addAssembly([bin 'OpenSim.Core.dll']);
NET.addAssembly([bin 'OpenSim.Optics.dll']);

% System data: entrance pupil 10 mm (the default aperture type), fields at 0 and 3 degrees,
% the F, d and C lines read from OpenSim's table (SCHOTT TIE-29, Table 2.3-1), d primary.
sys = OpenSim.Core.Model.OpticsSystemSettings();
sys.ApertureValue = 10;
fields = NET.createGeneric('System.Collections.Generic.List', {'OpenSim.Core.Model.OpticsFieldSettings'}, 2);
for y = [0 3]
    f = OpenSim.Core.Model.OpticsFieldSettings(); f.Y = y; fields.Add(f);
end
sys.Fields = fields;
waves = NET.createGeneric('System.Collections.Generic.List', {'OpenSim.Core.Model.OpticsWavelengthSettings'}, 3);
lines = {OpenSim.Optics.SpectralLines.F, OpenSim.Optics.SpectralLines.LowerD, OpenSim.Optics.SpectralLines.C};
for i = 1:3
    w = OpenSim.Core.Model.OpticsWavelengthSettings(); w.Micrometers = lines{i}.Micrometers; waves.Add(w);
end
sys.Wavelengths = waves;
sys.PrimaryWavelength = 2;

% The lens data: object at infinity; a 4 mm singlet of fused silica (refractiveindex.info's
% SiO2, Malitson, from the built-in catalogs) with the stop on its front; both radii variable;
% the back distance a marginal-ray-height solve (height 0: the paraxial focus).
surfaces = NET.createGeneric('System.Collections.Generic.List', {'OpenSim.Core.Model.OpticsSurfaceSettings'}, 4);
obj = OpenSim.Core.Model.OpticsSurfaceSettings(); obj.ThicknessIsInfinite = true;
front = OpenSim.Core.Model.OpticsSurfaceSettings();
front.Radius = 50; front.Thickness = 4; front.Material = 'SiO2-Malitson'; front.IsStop = true;
back = OpenSim.Core.Model.OpticsSurfaceSettings();
back.Radius = -200; back.Thickness = 95;
img = OpenSim.Core.Model.OpticsSurfaceSettings();
v1 = OpenSim.Core.Model.OpticsCellFlagSettings(); v1.Column = 'Radius'; v1.Kind = 'Variable';
v2 = OpenSim.Core.Model.OpticsCellFlagSettings(); v2.Column = 'Radius'; v2.Kind = 'Variable';
focus = OpenSim.Core.Model.OpticsCellFlagSettings();
focus.Column = 'Thickness'; focus.Kind = 'Solve'; focus.Solve = 'Marginal ray height';
p = NET.createGeneric('System.Collections.Generic.List', {'System.Double'}, 2); p.Add(0); p.Add(0);
focus.SolveParameters = p;
frontFlags = NET.createGeneric('System.Collections.Generic.List', {'OpenSim.Core.Model.OpticsCellFlagSettings'}, 1);
frontFlags.Add(v1); front.Flags = frontFlags;
backFlags = NET.createGeneric('System.Collections.Generic.List', {'OpenSim.Core.Model.OpticsCellFlagSettings'}, 2);
backFlags.Add(v2); backFlags.Add(focus); back.Flags = backFlags;
surfaces.Add(obj); surfaces.Add(front); surfaces.Add(back); surfaces.Add(img);

optics = OpenSim.Core.Model.OpticsSettings();
optics.SystemData = sys;
optics.Surfaces = surfaces;
catalogs = OpenSim.Optics.Materials.GlassCatalogLibrary.BuiltInCatalogs;

% The merit function: EFFL at the primary wavelength held at 100 mm (weight 1), then the
% Optimization Wizard's default rows (RMS spot radius about the centroid, Gaussian quadrature).
% MeritOperandRow.Of's six data values are passed: the interop does not fill a params array.
rows = NET.createGeneric('System.Collections.Generic.List', {'OpenSim.Optics.Merit.MeritOperandRow'}, 1);
rows.Add(OpenSim.Optics.Merit.MeritOperandRow.Of('EFFL', 0, 2, 100, 1, [0 0 0 0 0 0]));
optics.MeritFunction = OpenSim.Optics.Merit.MeritFunctionSettings.ToSettings(OpenSim.Optics.Merit.MeritFunction(rows));
optics = OpenSim.Optics.Scripting.OpticsScripting.WithDefaultMerit(optics, catalogs, []);
before = OpenSim.Optics.Scripting.OpticsScripting.EvaluateMerit(optics, catalogs);
fprintf('RESULT merit rows = %d, start merit = %.6g\n', before.Rows.Length, before.Value);

% The local optimizer (damped least squares, automatic cycles).
opt = OpenSim.Optics.Optimization.LocalOptimizer.Run(optics, catalogs);
fprintf('RESULT merit %.6g -> %.6g after %d cycles (%s)\n', opt.StartMerit, opt.Merit, opt.Cycles, char(opt.Stop.ToString()));
optimized = opt.Optimized;
fprintf('RESULT R1 = %.6g mm, R2 = %.6g mm\n', optimized.Surfaces.Item(1).Radius.Value, optimized.Surfaces.Item(2).Radius.Value);

% One real ray, the marginal ray of the edge field at the primary wavelength, with OpticStudio's
% RAYE and RAYV codes (0 and 0: it reached the image unvignetted).
ray = OpenSim.Optics.Scripting.OpticsScripting.TraceRay(optimized, catalogs, 0, 1, 0, 1, 2);
fprintf('RESULT marginal ray of field 2: RAYE = %d, RAYV = %d\n', ray.Error, ray.Vignetted);

% Quick Sensitivity of the optimised lens: each radius +-0.5 mm, RMS spot radius, paraxial focus.
trows = NET.createGeneric('System.Collections.Generic.List', {'OpenSim.Optics.Tolerancing.ToleranceOperandRow'}, 2);
for s = 1:2
    t = OpenSim.Optics.Tolerancing.ToleranceOperandRow();
    t.Code = 'TRAD'; t.Int1 = s; t.Int2 = 0; t.Min = -0.5; t.Max = 0.5;
    trows.Add(t);
end
tol = OpenSim.Optics.Tolerancing.ToleranceAnalysis.QuickSensitivity(optimized, OpenSim.Optics.Tolerancing.ToleranceTable(trows), catalogs);
fprintf('RESULT nominal RMS spot radius = %.6g %s\n', tol.Nominal, char(tol.Unit));
for r = 1:tol.Rows.Length
    row = tol.Rows(r);
    fprintf('RESULT tolerance row %d (%s): change %.6g at min, %.6g at max\n', row.Row, char(row.Code), row.AtMin.Change, row.AtMax.Change);
end

% The FFT MTF of the optimised lens, by its registry id, sampling 64 x 64, polychromatic, both
% fields. .NET arrays index from 1 with parentheses (result.Plots(1)), lists from 0 with Item,
% and double() turns a System.Double[] into a numeric vector.
settings = OpenSim.Optics.Scripting.OpticsScripting.AnalysisSettings('FftMtf');
settings.Sampling = 64;
result = OpenSim.Optics.Scripting.OpticsScripting.Analyze(optimized, catalogs, 'FftMtf', settings);
plt = result.Plots(1);
figure; hold on;
names = {};
for k = 0:plt.Curves.Count - 1
    c = plt.Curves.Item(k);
    x = double(c.X); yv = double(c.Y);
    plot(x, yv);
    names{end + 1} = char(c.Name);
    fprintf('RESULT %s: MTF(0) = %.6g, min = %.6g, max = %.6g\n', char(c.Name), yv(1), min(yv), max(yv));
end
legend(names);
xlabel(char(plt.XLabel)); ylabel(char(plt.YLabel)); title(char(plt.Title));
exportfigure('optics_from_jgraph_mtf.png');
disp('DONE');
