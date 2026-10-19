function g = p1_geom(p, t)
% P1_GEOM  Per-element area, centroid and shape-function gradients of linear triangles.
x1 = p(1, t(1, :)); y1 = p(2, t(1, :));
x2 = p(1, t(2, :)); y2 = p(2, t(2, :));
x3 = p(1, t(3, :)); y3 = p(2, t(3, :));
twoA = (x2 - x1) .* (y3 - y1) - (x3 - x1) .* (y2 - y1);
g.area = abs(twoA) / 2;
g.bx = [y2 - y3; y3 - y1; y1 - y2] ./ twoA;   % dN/dx
g.by = [x3 - x2; x1 - x3; x2 - x1] ./ twoA;   % dN/dy
g.cx = (x1 + x2 + x3) / 3;
g.cy = (y1 + y2 + y3) / 3;
g.np = size(p, 2);
g.t = t;
end
