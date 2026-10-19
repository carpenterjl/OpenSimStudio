function Q = acQuad(msh, elems, u, kap, nGauss)
%ACQUAD Integrals of a quadratic-triangle FE field over a set of elements.
%   Q = acQuad(msh, elems, u, kap) integrates over the elements ELEMS of the
%   PDE Toolbox mesh MSH (GeometricOrder 'quadratic', node order: 1-3
%   corners, 4 = mid 1-2, 5 = mid 2-3, 6 = mid 3-1) using the isoparametric
%   map, so the curved boundary elements the mesher puts on arcs are
%   integrated on their own geometry.  Returned fields:
%     area = int 1,  Iu = int u,  Iu2 = int |u|^2,
%     Ig   = int |grad u|^2,      Ip  = int |1 - 1j*kap*u|^2
%   The rule is a collapsed (Duffy) product of nGauss-point Gauss-Legendre
%   rules (default 5: exact for polynomials of total degree <= 8 on a
%   straight-sided triangle); nodes and weights are computed here by
%   Golub-Welsch, no tabulated constants.
if nargin < 5, nGauss = 5; end
[xi, eta, wq] = triRule(nGauss);
P = msh.Nodes;
E = msh.Elements(:, elems);
ne = size(E, 2);
X = reshape(P(1, E), 6, ne);
Y = reshape(P(2, E), 6, ne);
U = reshape(u(E), 6, ne);
Q = struct('area', 0, 'Iu', 0, 'Iu2', 0, 'Ig', 0, 'Ip', 0);
for q = 1:numel(wq)
    [N, Nx, Ne] = shape6(xi(q), eta(q));
    xx = Nx.' * X;  xe = Ne.' * X;
    yx = Nx.' * Y;  ye = Ne.' * Y;
    detJ = xx .* ye - xe .* yx;
    uq = N.' * U;
    ux_ = Nx.' * U;  ue_ = Ne.' * U;
    ux = ( ye .* ux_ - yx .* ue_) ./ detJ;
    uy = (-xe .* ux_ + xx .* ue_) ./ detJ;
    wJ = wq(q) * abs(detJ);
    Q.area = Q.area + sum(wJ);
    Q.Iu   = Q.Iu   + sum(wJ .* uq);
    Q.Iu2  = Q.Iu2  + sum(wJ .* abs(uq).^2);
    Q.Ig   = Q.Ig   + sum(wJ .* (abs(ux).^2 + abs(uy).^2));
    Q.Ip   = Q.Ip   + sum(wJ .* abs(1 - 1j*kap*uq).^2);
end
end

function [N, Nx, Ne] = shape6(x, e)
L1 = 1 - x - e; L2 = x; L3 = e;
N = [L1*(2*L1-1); L2*(2*L2-1); L3*(2*L3-1); 4*L1*L2; 4*L2*L3; 4*L3*L1];
% d/dxi: dL1=-1, dL2=1, dL3=0 ; d/deta: dL1=-1, dL2=0, dL3=1
Nx = [-(4*L1-1); (4*L2-1); 0; 4*(L1 - L2); 4*L3; -4*L3];
Ne = [-(4*L1-1); 0; (4*L3-1); -4*L2; 4*L2; 4*(L1 - L3)];
end
