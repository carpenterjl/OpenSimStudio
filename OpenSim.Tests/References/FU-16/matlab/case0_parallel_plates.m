% case0_parallel_plates.m - setup validation on MagneticsTests.ParallelPlates
% (OpenSim.Tests/Solvers/MagneticsTests.cs lines 23-49): planar, domain
% x -5..5 mm, y 0..10 mm, A = 0 on the left side only (bottom, top, right natural),
% go x -1.5..-1 mm, return 1..1.5 mm (full height), gap air, 1 A.
% Exact: L' = mu0*(s + 2t/3)/W.
mu0 = 4e-7*pi;
s = 2e-3; t = 0.5e-3; W = 10e-3; I = 1;
domain = [-5e-3 5e-3 0 W];
reg = struct('name', {'go','return','gap'}, ...
  'x0', {-s/2 - t, s/2, -s/2}, 'x1', {-s/2, s/2 + t, s/2}, ...
  'y0', {0, 0, 0}, 'y1', {W, W, W}, 'mur', {1, 1, 1}, ...
  'J', {I/(t*W), -I/(t*W), 0});
expected = mu0 * (s + 2*t/3) / W;
fid = fopen('case0_parallel_plates.csv', 'w');
fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n');
mm_csv(fid, 'case0_parallel_plates', 'exact', 'L_per_m_H', expected, '-', 0);
fprintf('exact L'' = %.10e H/m\n', expected);
R = mm_trirule(4);
hs = [0.5e-3 0.25e-3 0.125e-3];
for lev = 1:numel(hs)
  opts = struct('axi', false, 'dirichlet', 'L', 'hmax', hs(lev), 'hregion', [NaN NaN NaN]);
  S = mm_ms_solve(reg, domain, opts);
  nu = 1/mu0;
  lam = 0;
  for k = 1:2
    el = find(S.elemRegion == k);
    Q = mm_quad(S.msh, el, S.u, R);
    area = sum(Q.w(:));
    lam = lam + sign(reg(k).J) * sum(Q.w(:) .* Q.u(:)) / area;
  end
  Q = mm_quad(S.msh, 1:S.elements, S.u, R);
  Wm = 0.5 * nu * sum(Q.w(:) .* (Q.ux(:).^2 + Q.uy(:).^2));
  L = lam / I; LW = 2*Wm / I^2;
  fprintf('level %d: nodes %d, L'' flux %.10e, energy %.10e, rel err %.3e / %.3e (%.1f s)\n', ...
    lev, S.nodes, L, LW, L/expected - 1, LW/expected - 1, S.time);
  mm_csv(fid, 'case0_parallel_plates', 'I=1A', 'L_per_m_flux_H', L, lev, S.nodes);
  mm_csv(fid, 'case0_parallel_plates', 'I=1A', 'L_per_m_energy_H', LW, lev, S.nodes);
  mm_csv(fid, 'case0_parallel_plates', 'I=1A', 'rel_err_flux', L/expected - 1, lev, S.nodes);
end
fclose(fid);
