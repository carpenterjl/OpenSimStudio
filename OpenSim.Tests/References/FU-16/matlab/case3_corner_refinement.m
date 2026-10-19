function case3_corner_refinement(NIs, lev, cornerFactor)
%CASE3_CORNER_REFINEMENT (convergence probe) the level-lev ER mesh with only the
% eight window corners refined further (vertex size 0.05 mm/f/cornerFactor):
% how much of the remaining change sits at the re-entrant corners.
% FU-16's ER gate uses lev = 4, cornerFactor = 16 (rows 'probe_er_corner_refine', corners/16, level 4
% of case3_corner_refinement.csv); the case label written is unchanged from the probe's.
M = er_model(1, 1);
f = 2^(lev - 1);
hreg = nan(1, numel(M.regions));
hreg(M.core) = 0.5e-3 / f; hreg([M.winP M.winM]) = 0.5e-3 / f;
G = mm_mesh(M.regions, M.domain, struct('hmax', 3e-3 / f, 'hregion', hreg, ...
  'hvertex', [M.windowCorners, repmat(0.05e-3/f/cornerFactor, 8, 1)], 'hgrad', 1.3));
fn = 'case3_corner_refinement.csv';
newFile = ~isfile(fn);
fid = fopen(fn, 'a');
if newFile, fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n'); end
for NI = NIs
  o = er_solve(NI, lev, 'pchip', G);
  fprintf('corners x%g finer, NI %g level %d: nodes %d, lambda %.9e, W %.9e, Bleg %.6f, Bpost %.6f, Bmax %.4f [%d it, %.1f s]\n', ...
    cornerFactor, NI, lev, o.nodes, o.lambda, o.W, o.Bleg, o.Bpost, o.Bmax, o.iterations, o.time);
  p = sprintf('NI=%gA corners/%g', NI, cornerFactor);
  mm_csv(fid, 'probe_er_corner_refine', p, 'lambda_Wb_per_m', o.lambda, lev, o.nodes);
  mm_csv(fid, 'probe_er_corner_refine', p, 'W_J_per_m', o.W, lev, o.nodes);
end
fclose(fid);
end
