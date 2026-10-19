function case3_er_nonlinear(NIs, levels, variant)
%CASE3_ER_NONLINEAR Nonlinear planar ER leg section (ErCore.PlanarLegSection)
% at the given ampere-turns and mesh levels; B-H interpolant variant 'pchip'
% (the gate's) or 'opensim' (sensitivity). Appends to case3_er_nonlinear.csv.
% Per run: flux linkage per metre of the window coil (1 turn carrying NI),
% stored energy per metre W' = int w(|B|) dA with w = int_0^B H dB (coenergy
% reported separately), peak |B| in the core (all points; outside 0.5 mm and
% 1 mm of the eight re-entrant window corners), mean B across the right leg and
% the post at mid-height, the core area above the table's last B.
if nargin < 3, variant = 'pchip'; end
fn = 'case3_er_nonlinear.csv';
newFile = ~isfile(fn);
fid = fopen(fn, 'a');
if newFile, fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n'); end
for lev = levels
  G = [];
  for NI = NIs
    o = er_solve(NI, lev, variant, G);
    G = o.G;
    fprintf(['%s NI %g level %d: nodes %d, lambda %.9e Wb/m, W %.9e J/m (coenergy %.9e), Bmax %.4f, excl0.5mm %.5f, excl1mm %.5f, ' ...
      'Bleg %.6f, Bpost %.6f, area>table %.3e mm^2 [%d it, resid %.1e, step %.1e, %.1f s]\n'], variant, NI, lev, o.nodes, o.lambda, o.W, o.Wco, ...
      o.Bmax, o.Bmax_excl05, o.Bmax_excl10, o.Bleg, o.Bpost, o.areaAboveTable*1e6, o.iterations, o.resid, o.step, o.time);
    cn = ['case3_er_nonlinear_' variant];
    p = sprintf('NI=%gA', NI);
    mm_csv(fid, cn, p, 'lambda_Wb_per_m', o.lambda, lev, o.nodes);
    mm_csv(fid, cn, p, 'W_J_per_m', o.W, lev, o.nodes);
    mm_csv(fid, cn, p, 'coenergy_J_per_m', o.Wco, lev, o.nodes);
    mm_csv(fid, cn, p, 'Bmax_core_T', o.Bmax, lev, o.nodes);
    mm_csv(fid, cn, p, 'Bmax_core_excl0.5mm_T', o.Bmax_excl05, lev, o.nodes);
    mm_csv(fid, cn, p, 'Bmax_core_excl1mm_T', o.Bmax_excl10, lev, o.nodes);
    mm_csv(fid, cn, p, 'Bmean_leg_T', o.Bleg, lev, o.nodes);
    mm_csv(fid, cn, p, 'Bmean_post_T', o.Bpost, lev, o.nodes);
    mm_csv(fid, cn, p, 'core_area_above_table_m2', o.areaAboveTable, lev, o.nodes);
    mm_csv(fid, cn, p, 'newton_iterations', o.iterations, lev, o.nodes);
    mm_csv(fid, cn, p, 'newton_final_rel_residual', o.resid, lev, o.nodes);
  end
end
fclose(fid);
end
