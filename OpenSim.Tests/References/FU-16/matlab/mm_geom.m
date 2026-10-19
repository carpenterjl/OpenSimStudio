function dl = mm_geom(regions, domain)
%MM_GEOM Decomposed geometry of a rectangular domain and rectangular regions.
% regions: struct array with fields x0,x1,y0,y1. domain: [x0 x1 y0 y1].
% Overlaps are resolved into minimal faces by decsg; which region owns a face
% is decided afterwards (mm_classify), later regions taking precedence as in
% OpenSim's MagneticModel2D.RegionAt.
n = numel(regions) + 1;
gd = zeros(10, n);
rects = [domain; reshape([regions.x0; regions.x1; regions.y0; regions.y1], 4, [])'];
names = cell(1, n);
for k = 1:n
  r = rects(k,:);
  gd(:,k) = [3; 4; r(1); r(2); r(2); r(1); r(3); r(3); r(4); r(4)];
  names{k} = sprintf('R%d', k);
end
ns = char(names)';
sf = strjoin(names, '+');
dl = decsg(gd, sf, ns);
end
