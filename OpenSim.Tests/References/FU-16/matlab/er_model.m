function M = er_model(NI, coreMur)
%ER_MODEL ErCore.PlanarLegSection (OpenSim.Pcb/Magnetics/ErCore.cs lines 42-72)
% for ErCore(PostRadius 4e-3, WindowWidth 6e-3, LegWidth 3e-3, Depth 12e-3,
% PlateThickness 3e-3, WindowHeight 8e-3, CentreGap 0, material):
% planar, per metre of depth; rw = 10 mm, outer = 13 mm, halfWindow = 4 mm,
% top = 7 mm; domain x +-3*outer, y +-(top + 2*outer), A = 0 on all four sides
% (NaturalSides none). Regions in OpenSim's order: plate bottom, plate top, leg
% left, leg right, post (no gap), window right (air, +NI/A), window left (air,
% -NI/A) - StrandedCoil "window", Turns 1, Current NI.
% coreMur: number or @(location,state) handle.
post = 4e-3; ww = 6e-3; leg = 3e-3; plate = 3e-3; wh = 8e-3;
rw = post + ww; outer = rw + leg; hw = wh/2; top = hw + plate;
M.domain = [-3*outer, 3*outer, -top - 2*outer, top + 2*outer];
Aw = ww * wh;
mk = @(name, x0, x1, y0, y1, mur, J, kind) struct('name', name, 'x0', x0, 'x1', x1, 'y0', y0, 'y1', y1, 'mur', mur, 'J', J, 'kind', kind);
reg = [ ...
  mk('plate bottom', -outer, outer, -top, -hw, coreMur, 0, 'core'), ...
  mk('plate top', -outer, outer, hw, top, coreMur, 0, 'core'), ...
  mk('leg left', -outer, -rw, -hw, hw, coreMur, 0, 'core'), ...
  mk('leg right', rw, outer, -hw, hw, coreMur, 0, 'core'), ...
  mk('post', -post, post, -hw, hw, coreMur, 0, 'core'), ...
  mk('window right', post, rw, -hw, hw, 1, NI/Aw, 'win+'), ...
  mk('window left', -rw, -post, -hw, hw, 1, -NI/Aw, 'win-')];
M.regions = reg;
M.core = find(strcmp({reg.kind}, 'core'));
M.winP = find(strcmp({reg.kind}, 'win+'));
M.winM = find(strcmp({reg.kind}, 'win-'));
M.Aw = Aw; M.post = post; M.rw = rw; M.outer = outer; M.hw = hw; M.top = top;
% re-entrant (window) corners of the core and the outer corners
M.windowCorners = [post hw; post -hw; rw hw; rw -hw; -post hw; -post -hw; -rw hw; -rw -hw];
M.outerCorners = [outer top; outer -top; -outer top; -outer -top];
end
