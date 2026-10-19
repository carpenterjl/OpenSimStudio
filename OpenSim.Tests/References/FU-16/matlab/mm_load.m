function F = mm_load(msh, elems, g, R)
%MM_LOAD Consistent load vector F_i = int g(x,y) N_i dA over the given elements
% (6-node quadratic triangles), g evaluated at the quadrature points.
if nargin < 4, R = mm_trirule(4); end
T = msh.Elements(:, elems);
X = msh.Nodes(1,:); Y = msh.Nodes(2,:);
x1 = X(T(1,:)); x2 = X(T(2,:)); x3 = X(T(3,:));
y1 = Y(T(1,:)); y2 = Y(T(2,:)); y3 = Y(T(3,:));
det = (x2 - x1) .* (y3 - y1) - (x3 - x1) .* (y2 - y1);
L2 = R.xi; L3 = R.eta; L1 = 1 - L2 - L3;
N = [L1.*(2*L1-1), L2.*(2*L2-1), L3.*(2*L3-1), 4*L1.*L2, 4*L2.*L3, 4*L3.*L1];   % nq x 6
xq = L1 * x1 + L2 * x2 + L3 * x3;
yq = L1 * y1 + L2 * y2 + L3 * y3;
G = g(xq, yq) .* (R.w * abs(det));      % nq x nE
Fe = N' * G;                            % 6 x nE
F = accumarray(T(:), Fe(:), [size(msh.Nodes, 2), 1]);
end
