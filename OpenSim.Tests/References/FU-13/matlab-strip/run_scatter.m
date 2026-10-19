function run_scatter(part)
% run_scatter.m - with the buffer rings (stripRac's default), the level-3 mesh-perturbation
% scatter for case c (RfarFactor, CornerFactor and Hgrad varied), appended to
% strip_rac_scatter.csv; part 1/2/3 runs t/d = 2/4/8. Column diff_from_old_L4 is against the
% level-4 values of earlier main runs, not kept (t/d = 2: 1.856217344; t/d = 4: 3.318714722).
sigma = 5.8e7; mu0 = 4e-7*pi; w = 1e-3; t = 35e-6;
fn = 'strip_rac_scatter.csv';
if ~isfile(fn)
    fid = fopen(fn, 'w');
    fprintf(fid, 'case,t_over_delta,mesh_level,RfarFactor,CornerFactor,Hgrad,nodes,nRings,Rac_over_Rdc,diff_from_old_L4\n');
    fclose(fid);
end
if part == 1
    r = 2; ref = 1.856217344;
    variants = {20,0.25,1.3; 20,1,1.297; 20,1,1.3; 20,0.25,1.3015; 20,0.25,1.2985; 21,0.25,1.3; 22,0.25,1.3; 20,0.5,1.3; 20,1,1.3045};
elseif part == 2
    r = 4; ref = 3.318714722;
    variants = {20,0.25,1.3; 21,0.25,1.3; 20,0.25,1.297; 20,0.25,1.3015; 20,1,1.3};
else
    % t/d = 8; the reference here is the buffered level-4 run (6.745849412)
    r = 8; ref = 6.745849412;
    variants = {21,0.25,1.3; 20,0.5,1.3; 20,0.25,1.297};
end
f = r^2/(pi*mu0*sigma*t^2);
for k = 1:size(variants, 1)
    [rf, cf, hg] = variants{k, :};
    [ra, ~, info] = stripRac(w, t, sigma, f, 1/4, 'DcSolve', false, 'RfarFactor', rf, 'CornerFactor', cf, 'Hgrad', hg);
    fprintf('c t/d=%g L3 Rfar %gx corner %.2f Hgrad %.4f nodes %7d rings %d R/Rdc %.9f diff %+.2e (%.1f s)\n', ...
        r, rf, cf, hg, info.nodes, info.nRings, ra, ra - ref, info.tMesh + info.tSolve);
    fid = fopen(fn, 'a');
    fprintf(fid, 'c,%g,3,%g,%g,%g,%d,%d,%.10f,%.4e\n', r, rf, cf, hg, info.nodes, info.nRings, ra, ra - ref);
    fclose(fid);
end
end
