% runs = [R s; R s; ...]; outname = 'team28_xxx.json'
res = [];
for k = 1:size(runs, 1)
    r = solve_team28(runs(k, 1), runs(k, 2));
    disp(r);
    res = [res, r]; %#ok<AGROW>
    fid = fopen(outname, 'w'); fprintf(fid, '%s', jsonencode(res, 'PrettyPrint', true)); fclose(fid);
end
