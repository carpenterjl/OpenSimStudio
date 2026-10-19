% t_meshstats.m - mesh sizes and memory estimates (no solves) for candidate mesh densities
set(0,'DefaultFigureVisible','off');
fprintf('FILTER\n');
for gx = [3.948 10]
  for mel = [1.0 0.8 0.7 0.6]
    o.gx = gx; p = build_filter(o);
    md = mesh(p,'MaxEdgeLength',mel*1e-3); close all
    fprintf('gx %6.3f  mel %.2f mm: tri %5d tet %6d  mem %s\n', gx, mel, size(md.Triangles,2), size(md.Tetrahedra,2), memoryEstimate(p));
  end
end
fprintf('PATCH\n');
clear o
for m = [16.75 33.5]
  for mel = [4 3 2.5 2 1.6]
    o.m = m; p = build_patch(o);
    md = mesh(p,'MaxEdgeLength',mel*1e-3); close all
    fprintf('m %6.2f  mel %.2f mm: tri %5d tet %6d  mem %s\n', m, mel, size(md.Triangles,2), size(md.Tetrahedra,2), memoryEstimate(p));
  end
end
p = build_patch();
fprintf('auto mesh at 2.5 GHz memory: %s\n', memoryEstimate(p, 2.5e9, RetainMesh=true));
md = mesh(p); close all
fprintf('auto mesh: tri %d tet %d maxedge %g minedge %g\n', size(md.Triangles,2), size(md.Tetrahedra,2), md.MaxEdgeLength, md.MinEdgeLength);
p = build_filter();
fprintf('filter auto mesh at 20 GHz memory: %s\n', memoryEstimate(p, 20e9, RetainMesh=true));
md = mesh(p); close all
fprintf('filter auto mesh: tri %d tet %d maxedge %g minedge %g\n', size(md.Triangles,2), size(md.Tetrahedra,2), md.MaxEdgeLength, md.MinEdgeLength);
