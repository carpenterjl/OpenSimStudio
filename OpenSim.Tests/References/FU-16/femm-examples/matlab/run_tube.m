% runs = [Lbox_in Npoly s; ...]; outname
res = [];
for k = 1:size(runs, 1)
    r = solve_tube(runs(k, 1), runs(k, 2), runs(k, 3));
    disp(r);
    res = [res, r]; %#ok<AGROW>
    fid = fopen(outname, 'w'); fprintf(fid, '%s', jsonencode(res, 'PrettyPrint', true)); fclose(fid);
end
exit(0);
