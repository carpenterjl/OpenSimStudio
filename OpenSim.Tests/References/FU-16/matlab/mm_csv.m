function mm_csv(fid, caseName, parameter, quantity, value, level, nodes)
%MM_CSV One row: case, parameter, quantity, value, mesh_level, nodes.
fprintf(fid, '%s,%s,%s,%.12g,%s,%d\n', caseName, parameter, quantity, value, num2str(level), nodes);
end
