function case1_potcore(gaps, levels)
%CASE1_POTCORE Static, linear, axisymmetric pot core (femodel magnetostatic,
% PlanarType axisymmetric). 4 stranded 1-turn coils in series, 1 A each,
% ferrite mu_r 2000. L = sum of the turns' flux linkage / I, and W = (1/2)int B.H dV.
% Writes case1_potcore_g<gap um>.csv.
if nargin < 1, gaps = [0.1e-3 0.2e-3 0.4e-3]; end
if nargin < 2, levels = 1:4; end
mu0 = 4e-7*pi; I = 1;
At = 0.8e-3 * 35e-6;
R = mm_trirule(4);
for gap = gaps
  fn = sprintf('case1_potcore_g%d.csv', round(gap*1e6));
  newFile = ~isfile(fn);
  fid = fopen(fn, 'a');
  if newFile, fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n'); end
  M = potcore_model(gap, true, 2000, I/At);
  for lev = levels
    f = 2^(lev - 1);
    nreg = numel(M.regions);
    hreg = nan(1, nreg);
    hreg(M.core) = 0.5e-3 / f;
    hreg(M.turns) = 17.5e-6 / f;
    hreg(strcmp({M.regions.kind}, 'gap')) = gap / 4 / f;
    hreg(strcmp({M.regions.kind}, 'box')) = 0.2e-3 / f;     % the window air (mesh only)
    opts = struct('axi', true, 'dirichlet', 'TBR', 'hmax', 4e-3 / f, 'hregion', hreg, ...
      'hvertex', [M.corners(1:2,:), repmat(gap/16/f, 2, 1); M.corners(3:4,:), repmat(gap/4/f, 2, 1); ...
                  M.windowCorners, repmat(0.1e-3/f, 4, 1)]);
    S = mm_ms_solve(M.regions, M.domain, opts);
    % flux linkage: lambda_i = 2*pi*(1/A_i) int_i psi dA  (MagneticSolution2D.FluxLinkage)
    lam = 0;
    for k = M.turns
      Q = mm_quad(S.msh, find(S.elemRegion == k), S.u, R);
      lam = lam + 2*pi * sum(Q.w(:) .* Q.u(:)) / sum(Q.w(:));
    end
    % energy: W = pi * int nu |grad psi|^2 / r dA
    nuE = ones(1, S.elements) / mu0;
    isCore = ismember(S.elemRegion, M.core);
    nuE(isCore) = 1 / (mu0 * 2000);
    Q = mm_quad(S.msh, 1:S.elements, S.u, R);
    Wm = pi * sum(sum(Q.w .* (nuE .* (Q.ux.^2 + Q.uy.^2)) ./ Q.x));
    % peak |B| in the core and B at the gap centre on the axis
    Br = -Q.uy ./ Q.x; Bz = Q.ux ./ Q.x;
    Bmag = hypot(Br, Bz);
    Bcore = max(max(Bmag(:, isCore)));
    L = lam / I; LW = 2*Wm / I^2;
    fprintf('gap %.1f mm level %d: nodes %d, L %.8e H (energy %.8e, diff %.2e), W %.8e J, max|B| core %.4g T (%.1f s)\n', ...
      gap*1e3, lev, S.nodes, L, LW, LW/L - 1, Wm, Bcore, S.time);
    p = sprintf('gap=%gmm', gap*1e3);
    mm_csv(fid, 'case1_potcore_static', p, 'L_flux_H', L, lev, S.nodes);
    mm_csv(fid, 'case1_potcore_static', p, 'L_energy_H', LW, lev, S.nodes);
    mm_csv(fid, 'case1_potcore_static', p, 'W_J', Wm, lev, S.nodes);
    mm_csv(fid, 'case1_potcore_static', p, 'maxB_core_T', Bcore, lev, S.nodes);
  end
  fclose(fid);
end
end
