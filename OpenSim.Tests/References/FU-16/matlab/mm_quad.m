function Q = mm_quad(msh, elems, u, R)
%MM_QUAD Evaluate a 6-node quadratic (or 3-node linear) FE field u at the
% quadrature points of the given elements (straight-sided triangles).
% Returns nQ x nE arrays: x, y, w (physical weight), u, ux, uy.
% Node order (checked in probe_api.log): corners 1-3, then midsides 4 (1-2),
% 5 (2-3), 6 (3-1).
if nargin < 4, R = mm_trirule(4); end
T = msh.Elements(:, elems);
X = msh.Nodes(1,:); Y = msh.Nodes(2,:);
x1 = X(T(1,:)); x2 = X(T(2,:)); x3 = X(T(3,:));
y1 = Y(T(1,:)); y2 = Y(T(2,:)); y3 = Y(T(3,:));
det = (x2 - x1) .* (y3 - y1) - (x3 - x1) .* (y2 - y1);
L2 = R.xi; L3 = R.eta; L1 = 1 - L2 - L3;
nq = numel(L1);
if size(T,1) == 6
  N = [L1.*(2*L1-1), L2.*(2*L2-1), L3.*(2*L3-1), 4*L1.*L2, 4*L2.*L3, 4*L3.*L1];   % nq x 6
  dXi  = [-(4*L1-1), (4*L2-1), zeros(nq,1), 4*(L1 - L2), 4*L3, -4*L3];
  dEta = [-(4*L1-1), zeros(nq,1), (4*L3-1), -4*L2, 4*L2, 4*(L1 - L3)];
else
  N = [L1, L2, L3];
  dXi  = repmat([-1 1 0], nq, 1);
  dEta = repmat([-1 0 1], nq, 1);
end
U = u(T);                         % nn x nE (works for complex u)
if isrow(U), U = U(:); end
Q.u    = N * U;
uxi    = dXi * U;
ueta   = dEta * U;
Q.ux   = ((y3 - y1) .* uxi - (y2 - y1) .* ueta) ./ det;
Q.uy   = (-(x3 - x1) .* uxi + (x2 - x1) .* ueta) ./ det;
Q.x    = L1 * x1 + L2 * x2 + L3 * x3;
Q.y    = L1 * y1 + L2 * y2 + L3 * y3;
Q.w    = R.w * abs(det);
end
