function M = potcore_model(gap, withCore, mur, Jturn)
%POTCORE_MODEL The model PlanarWindingSection.Build (OpenSim.Pcb/Magnetics/
% PlanarWindingSection.cs lines 257-316) makes for the turns and core of
% MagneticsTests.GappedPotCore_InductanceFollowsTheGap (lines 287-306):
%  turns i = 0..3: R0 = 4e-3 + 1.2e-3 i, R1 = 4.8e-3 + 1.2e-3 i, Z0 = -35e-6, Z1 = 0
%  AxisymmetricCore(PostRadius 3e-3, WindowRadius 10e-3, OuterRadius 12e-3,
%                   PlateThickness 2e-3, WindowHeight 2e-3, CentreGap gap)
% Regions in OpenSim's order (core bottom, core top, core wall, post low, post
% high | post, then the turns). Meshing-only air regions are added: 'window'
% (or 'near' without a core) first and 'gap' last (air, no source: the same
% physics as OpenSim's background there; they only let the mesh be sized).
% Jturn: current density in every turn (stranded, 1 turn each), 0 for none.
if nargin < 3, mur = 2000; end
if nargin < 4, Jturn = 0; end
R0 = 4e-3 + (0:3)*1.2e-3; R1 = 4.8e-3 + (0:3)*1.2e-3; Z0 = -35e-6; Z1 = 0;
zMin = Z0; zMax = Z1; rMax = max(R1);
zMid = 0.5*(zMin + zMax);
post = 3e-3; win = 10e-3; outerR = 12e-3; plate = 2e-3; wh = 2e-3;
if withCore
  outer = outerR; halfHeight = wh/2 + plate;
else
  outer = rMax; halfHeight = max(zMax - zMin, rMax);
end
M.domain = [0, 4*outer, zMid - halfHeight - 3*outer, zMid + halfHeight + 3*outer];
M.zMid = zMid; M.gap = gap;
reg = struct('name', {}, 'x0', {}, 'x1', {}, 'y0', {}, 'y1', {}, 'mur', {}, 'J', {}, 'kind', {});
% Meshing-only air box (first, so every physical region takes precedence over
% it): the core's window, or without a core the winding's surroundings.
if withCore
  reg(end+1) = mk('window (mesh only)', post, win, zMid - wh/2, zMid + wh/2, 1, 0, 'box');
else
  reg(end+1) = mk('near (mesh only)', 0, 10e-3, zMid - 2e-3, zMid + 2e-3, 1, 0, 'box');
end
if withCore
  zb = zMid - wh/2; zt = zMid + wh/2;
  reg(end+1) = mk('core bottom', 0, outerR, zb - plate, zb, mur, 0, 'core');
  reg(end+1) = mk('core top', 0, outerR, zt, zt + plate, mur, 0, 'core');
  reg(end+1) = mk('core wall', win, outerR, zb, zt, mur, 0, 'core');
  if gap > 0
    reg(end+1) = mk('core post low', 0, post, zb, zMid - gap/2, mur, 0, 'core');
    reg(end+1) = mk('core post high', 0, post, zMid + gap/2, zt, mur, 0, 'core');
  else
    reg(end+1) = mk('core post', 0, post, zb, zt, mur, 0, 'core');
  end
  M.zb = zb; M.zt = zt;
  M.corners = [post, zMid - gap/2; post, zMid + gap/2; 0, zMid - gap/2; 0, zMid + gap/2];
  M.windowCorners = [post, zb; post, zt; win, zb; win, zt];
end
for i = 1:4
  reg(end+1) = mk(sprintf('P L1 r%.3f', R0(i)*1e3), R0(i), R1(i), Z0, Z1, 1, Jturn, 'turn'); %#ok<AGROW>
end
if withCore && gap > 0
  reg(end+1) = mk('gap (mesh only)', 0, post, zMid - gap/2, zMid + gap/2, 1, 0, 'gap');
end
M.regions = reg;
M.turns = find(strcmp({reg.kind}, 'turn'));
M.core = find(strcmp({reg.kind}, 'core'));
M.R0 = R0; M.R1 = R1; M.Z0 = Z0; M.Z1 = Z1;
end

function r = mk(name, x0, x1, y0, y1, mur, J, kind)
r = struct('name', name, 'x0', x0, 'x1', x1, 'y0', y0, 'y1', y1, 'mur', mur, 'J', J, 'kind', kind);
end
