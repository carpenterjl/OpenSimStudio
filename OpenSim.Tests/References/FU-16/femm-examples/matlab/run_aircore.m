% runs = [caseIndex mbox s; ...]; outname
cases = [100 1 2 1; 100 1 1.25 4; 100 0.25 2 0.25];
res = [];
for k = 1:size(runs, 1)
    c = cases(runs(k, 1), :);
    r = solve_aircore(c(1), c(2), c(3), c(4), runs(k, 2), runs(k, 3));
    r.case_index = runs(k, 1);
    disp(r);
    res = [res, r]; %#ok<AGROW>
    fid = fopen(outname, 'w'); fprintf(fid, '%s', jsonencode(res, 'PrettyPrint', true)); fclose(fid);
end
