% Finest level plus virtual-work force: F = (I^2/2) dL/dg by central difference on the gap.
res = [];
r = solve_induct1a(0.0625, 0); disp(r); res = [res, r];
dgs = [-0.001 0.001];
for s = [0.125 0.0625]
    for dg = dgs
        r = solve_induct1a(s, dg); disp(r); res = [res, r]; %#ok<AGROW>
    end
end
fid = fopen('induct1a_fe_vw.json', 'w'); fprintf(fid, '%s', jsonencode(res, 'PrettyPrint', true)); fclose(fid);
