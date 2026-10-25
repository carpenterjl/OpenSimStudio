% FU-46 check: JGraph drives OpenSim's libraries in-process through NET.addAssembly.
% Each assembly whose types the script names is added by itself (as MATLAB's NET.addAssembly works);
% the ones they depend on load with them.
% JGraph's load context does not probe the folder for an assembly's dependencies, so the libraries
% go in before the command-line assembly that references them.
bin = 'D:\EE Projects\OpenSimStudio\OpenSim.Cli\bin\Debug\net8.0\';
libs = {'OpenSim.Core', 'OpenSim.Geometry', 'OpenSim.Meshing', 'OpenSim.Solvers', 'OpenSim.Pcb', 'OpenSim.Rf', 'OpenSim.Cfd'};
for i = 1:numel(libs)
    NET.addAssembly([bin libs{i} '.dll']);
end
asm = NET.addAssembly([bin 'opensim.dll']);
disp(class(asm));

% A 10 x 10 x 20 mm copper bar, 300 K at z = 0 and 400 K at z = 20 mm, built from OpenSim's own types.
body = OpenSim.Core.Model.Body();
body.Name = 'Bar';
body.Geometry = OpenSim.Geometry.PrimitiveFactory.CreateBox(0.01, 0.01, 0.02);
body.GeometrySource = 'Box 10 x 10 x 20 mm';
mesh = OpenSim.Core.Model.MeshSettings();
mesh.TargetEdgeLength = 0.005;
mesh.Method = OpenSim.Core.Model.MeshMethod.StructuredLattice;
body.MeshSettings = mesh;
mat = OpenSim.Core.Model.Material();
mat.Name = 'TestCopper';
mat.YoungsModulus = 110e9; mat.PoissonRatio = 0.34; mat.Density = 8960;
mat.ThermalConductivity = 400; mat.SpecificHeat = 385; mat.ElectricalConductivity = 5.96e7;
body.Material = mat;
% FaceIds is an IReadOnlyList<int>: a List<int> (which implements it) is the value a script can hand over.
coldFaces = NET.createGeneric('System.Collections.Generic.List', {'System.Int32'}, 1); coldFaces.Add(int32(4));
hotFaces = NET.createGeneric('System.Collections.Generic.List', {'System.Int32'}, 1); hotFaces.Add(int32(5));
cold = OpenSim.Core.Model.FixedTemperature(); cold.Name = 'cold'; cold.FaceIds = coldFaces; cold.Kelvin = 300;
hot = OpenSim.Core.Model.FixedTemperature(); hot.Name = 'hot'; hot.FaceIds = hotFaces; hot.Kelvin = 400;
body.BoundaryConditions.Add(cold);
body.BoundaryConditions.Add(hot);
project = OpenSim.Core.Model.SimProject();
project.Name = 'Bar';
project.AnalysisType = 'Thermal';
project.Bodies.Add(body);

% Mesh and solve as the command line's `solve` does, the log into a JGS function handle.
settings = OpenSim.Cli.StudySettings.FromProject(project.Solver);
logLine = @(s) disp(['  log: ' char(s)]);
OpenSim.Cli.StudyRunner.MeshBodies(project, OpenSim.Cli.StudyRunner.DefaultMesher(), false, logLine);
run = OpenSim.Cli.StudyRunner.Solve(project, 'Thermal', OpenSim.Core.Persistence.MaterialLibrary(), settings, logLine);
outputs = OpenSim.Cli.StudyRunner.Outputs(run);
keys = outputs.Keys;
e = keys.GetEnumerator();
while e.MoveNext()
    k = e.Current;
    fprintf('RESULT %s = %.6g\n', char(k), outputs.Item(k));
end

% The impedance calculator, a plain library call.
line = OpenSim.Rf.Si.LineSpec();
line.WidthMeters = 2.413e-3; line.HeightMeters = 0.794e-3; line.ThicknessMeters = 1e-6;
line.RelativePermittivity = 2.2; line.LossTangent = 0; line.FrequencyHz = 3e9;
spec = OpenSim.Rf.Si.ImpedanceCalculator.Solve(line);
mode = spec.Modes.Item(0);
fprintf('RESULT Z0 = %.4f ohm, eeff = %.5f\n', mode.ImpedanceOhms, mode.EffectivePermittivity);
disp('DONE');
