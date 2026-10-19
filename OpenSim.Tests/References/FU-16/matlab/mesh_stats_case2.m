% mesh_stats_case2.m - actual element sizes in the copper of the case-2 meshes
% (the same meshing calls as case2_potcore_ac): largest and mean triangle edge
% (corner to corner) inside the turns, against delta/4.
mu0 = 4e-7*pi; sig = 5.8e7; gap = 0.2e-3;
delta = 1 ./ sqrt(pi * [100e3 1e6] * mu0 * sig);
fid = fopen('mesh_stats_case2.csv', 'w');
fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n');
for cfg = {'core', 'air'}
  withCore = strcmp(cfg{1}, 'core');
  M = potcore_model(gap, withCore, 2000, 0);
  reg = M.regions;
  for k = 1:numel(reg)
    if strcmp(reg(k).kind, 'turn'), reg(k).sigma = sig; else, reg(k).sigma = 0; end
  end
  turns = find(strcmp({reg.kind}, 'turn'));
  for lev = 1:3
    f = 2^(lev - 1);
    hreg = nan(1, numel(reg));
    hreg(turns) = 16.5e-6 / f;
    hreg(strcmp({reg.kind}, 'box')) = 0.1e-3 / f;
    if withCore
      hreg(strcmp({reg.kind}, 'core')) = 0.5e-3 / f;
      hreg(strcmp({reg.kind}, 'gap')) = gap / 4 / f;
      hv = [M.corners(1:2,:), repmat(gap/16/f, 2, 1); M.corners(3:4,:), repmat(gap/4/f, 2, 1); ...
            M.windowCorners, repmat(0.1e-3/f, 4, 1)];
    else
      hv = [];
    end
    S = mm_th_setup(reg, M.domain, struct('dirichlet', 'TBRL', 'hmax', 4e-3 / f, 'hregion', hreg, 'hvertex', hv, 'hgrad', 1.3));
    el = find(ismember(S.elemRegion, turns));
    P = S.msh.Nodes; T = S.msh.Elements(1:3, el);
    e1 = hypot(P(1,T(1,:)) - P(1,T(2,:)), P(2,T(1,:)) - P(2,T(2,:)));
    e2 = hypot(P(1,T(2,:)) - P(1,T(3,:)), P(2,T(2,:)) - P(2,T(3,:)));
    e3 = hypot(P(1,T(3,:)) - P(1,T(1,:)), P(2,T(3,:)) - P(2,T(1,:)));
    E = [e1 e2 e3];
    fprintf('%s level %d: nodes %d, copper triangles %d, largest edge %.2f um, mean edge %.2f um; delta/4 = %.2f um (100 kHz), %.2f um (1 MHz)\n', ...
      cfg{1}, lev, S.nodes, numel(el), max(E)*1e6, mean(E)*1e6, delta(1)/4*1e6, delta(2)/4*1e6);
    mm_csv(fid, ['mesh_case2_' cfg{1}], 'copper', 'largest_edge_m', max(E), lev, S.nodes);
    mm_csv(fid, ['mesh_case2_' cfg{1}], 'copper', 'mean_edge_m', mean(E), lev, S.nodes);
    mm_csv(fid, ['mesh_case2_' cfg{1}], 'copper', 'triangles', numel(el), lev, S.nodes);
  end
end
fclose(fid);
