function case4_rings(levels, bigBox)
%CASE4_RINGS MagneticsTests.CoaxialRings_HaveMaxwellsMutualAndTheThinRingSelfInductance
% (OpenSim.Tests/Solvers/MagneticsTests.cs lines 144-168): axisymmetric, domain
% r 0..0.15 m, z -0.15..0.15 m, A = 0 on top/bottom/right (axis pinned), ring1
% r 9.9..10.1 mm, z -0.1..0.1 mm carrying 1 A (stranded, uniform J); ring2 the
% same at z 4.9..5.1 mm, no current. L = lambda_1/I, M = lambda_2/I.
% Meshing: an air box r 6..14 mm, z -4..9 mm (mesh-only, no physics change)
% holds the near field at 0.5 mm/2^(lev-1); rings at c/8/2^(lev-1); Hgrad 1.2.
% bigBox = true adds one run (level 3 sizes) in a 10x larger A = 0 box, to
% show how much the 0.15 m box itself moves L and M (not part of the model).
if nargin < 1, levels = 1:4; end
if nargin < 2, bigBox = true; end
mu0 = 4e-7*pi; I = 1;
r = 10e-3; c = 0.2e-3; sep = 5e-3;
fid = fopen('case4_rings.csv', 'w');
fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n');
% closed forms the OpenSim test compares with (free space, not the A = 0 box)
k2 = 4*r*r / ((2*r)^2 + sep^2);
[K, E] = ellipke(k2);
k = sqrt(k2);
Mmax = mu0 * r * ((2/k - k)*K - 2/k*E);
gmd = 0.44705 * c;  % the test's GMD of a square section (MagneticsTests.cs line 163)
Lthin = mu0 * r * (log(8*r/gmd) - 2);
fprintf('Maxwell filament mutual (free space) %.8e H; thin-ring L with the test''s GMD %.8e H\n', Mmax, Lthin);
mm_csv(fid, 'case4_rings', 'closed_form_free_space', 'M_maxwell_filaments_H', Mmax, '-', 0);
mm_csv(fid, 'case4_rings', 'closed_form_free_space', 'L_thin_ring_gmd0.44705c_H', Lthin, '-', 0);
runs = num2cell(levels);
if bigBox, runs{end+1} = 'big'; end
for i = 1:numel(runs)
  if ischar(runs{i}), lev = 3; domain = [0 1.5 -1.5 1.5]; tag = 'box_r1.5m_z+-1.5m'; levLabel = 'L3-bigbox';
  else, lev = runs{i}; domain = [0 0.15 -0.15 0.15]; tag = 'ring1=1A'; levLabel = lev; end
  f = 2^(lev - 1);
  reg = struct('name', {'near (mesh only)','ring1','ring2'}, 'x0', {6e-3, r - c/2, r - c/2}, 'x1', {14e-3, r + c/2, r + c/2}, ...
    'y0', {-4e-3, -c/2, sep - c/2}, 'y1', {9e-3, c/2, sep + c/2}, 'mur', {1, 1, 1}, 'J', {0, I/c^2, 0});
  opts = struct('axi', true, 'dirichlet', 'TBR', 'hmax', 4e-3/f, 'hregion', [0.5e-3/f, c/8/f, c/8/f], 'hgrad', 1.2);
  if ischar(runs{i}), opts.hmax = 40e-3; end
  S = mm_ms_solve(reg, domain, opts);
  R = mm_trirule(4);
  lam = zeros(1, 2);
  for kk = 2:3
    Q = mm_quad(S.msh, find(S.elemRegion == kk), S.u, R);
    lam(kk-1) = 2*pi * sum(Q.w(:) .* Q.u(:)) / sum(Q.w(:));
  end
  Q = mm_quad(S.msh, 1:S.elements, S.u, R);
  Wm = pi / mu0 * sum(sum(Q.w .* (Q.ux.^2 + Q.uy.^2) ./ Q.x));
  fprintf('%s level %s: nodes %d, L %.9e H (2W/I^2 %.9e), M %.9e H (%.1f s)\n', tag, num2str(levLabel), S.nodes, lam(1)/I, 2*Wm/I^2, lam(2)/I, S.time);
  mm_csv(fid, 'case4_rings', tag, 'L_ring1_H', lam(1)/I, levLabel, S.nodes);
  mm_csv(fid, 'case4_rings', tag, 'L_ring1_energy_H', 2*Wm/I^2, levLabel, S.nodes);
  mm_csv(fid, 'case4_rings', tag, 'M_ring1_ring2_H', lam(2)/I, levLabel, S.nodes);
end
fclose(fid);
end
