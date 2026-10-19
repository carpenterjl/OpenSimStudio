function case3_outer_corners(NIs, lev)
%CASE3_OUTER_CORNERS (convergence probe) the ER section on the level-lev
% mesh with the four outer (convex) core corners refined like the eight window
% corners (0.05 mm/f): how much of the level-to-level change is the outer corners.
% Writes case3_outer_corner_probe.csv.
M = er_model(1, 1);
f = 2^(lev - 1);
hreg = nan(1, numel(M.regions));
hreg(M.core) = 0.5e-3 / f; hreg([M.winP M.winM]) = 0.5e-3 / f;
G = mm_mesh(M.regions, M.domain, struct('hmax', 3e-3 / f, 'hregion', hreg, ...
  'hvertex', [M.windowCorners, repmat(0.05e-3/f, 8, 1); M.outerCorners, repmat(0.05e-3/f, 4, 1)], 'hgrad', 1.3));
fn = 'case3_outer_corner_probe.csv';
newFile = ~isfile(fn);
fid = fopen(fn, 'a');
if newFile, fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n'); end
for NI = NIs
  o = er_solve(NI, lev, 'pchip', G);
  fprintf('outer corners refined, NI %g level %d: nodes %d, lambda %.9e, W %.9e, Bleg %.6f, Bpost %.6f [%d it, %.1f s]\n', ...
    NI, lev, o.nodes, o.lambda, o.W, o.Bleg, o.Bpost, o.iterations, o.time);
  mm_csv(fid, 'probe_er_outer_corners_refined', sprintf('NI=%gA', NI), 'lambda_Wb_per_m', o.lambda, lev, o.nodes);
  mm_csv(fid, 'probe_er_outer_corners_refined', sprintf('NI=%gA', NI), 'W_J_per_m', o.W, lev, o.nodes);
end
fclose(fid);
end
