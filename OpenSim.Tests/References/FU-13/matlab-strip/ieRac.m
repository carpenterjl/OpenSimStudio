function [ratio, info] = ieRac(w, t, sigma, f, level, varargin)
%IERAC Independent cross-check: volume integral equation for the AC
%   resistance of an isolated rectangular conductor (no air mesh, no far
%   boundary).  Per unit length, mu0 everywhere, e^{j omega t}:
%       J(r)/sigma + j*omega*A(r) = E0 + const,
%       A(r) = -(mu0/(2*pi)) * int_cond J(r') ln|r - r'| dS'
%   (the constant, i.e. the reference length of the logarithm, moves only
%   Im(E0/I)).  J is piecewise constant on a tensor grid of rectangular
%   cells, graded toward the outer surfaces; collocation at cell centres;
%   cell integrals of ln|r - r'| in closed form; quarter symmetry by images.
%   Dimensionless as in stripRac: lengths in L0 = min(w,t)/2,
%   kappa = omega*mu0*sigma*L0^2, j = J/(sigma*E0):
%       j_i - 1j*kappa/(2*pi) * sum_k K_ik j_k = 1,
%   ratio = Re(Area / S), S = int j dS.
%   level: each refinement splits every base cell into 2x2 (level 1 = base).
%   Options: 'Hs' surface cell size / min(delta, t/2) at level 1 (0.25),
%            'Q' growth ratio of the base grid (1.25), 'CapFrac' largest
%            base cell / half-dimension (1/6).
p = inputParser;
addParameter(p, 'Hs', 0.25);
addParameter(p, 'Q', 1.25);
addParameter(p, 'CapFrac', 1/6);
parse(p, varargin{:});
o = p.Results;
mu0 = 4e-7*pi;
omega = 2*pi*f;
delta = sqrt(2/(omega*mu0*sigma));
L0 = min(w, t)/2;
W = (w/2)/L0; T = (t/2)/L0;
kap = omega*mu0*sigma*L0^2;
dS = delta/L0;
hs = o.Hs*min(dS, min(W, T));
xe = grid1d(W, hs, o.Q, o.CapFrac*W, level);
ye = grid1d(T, hs, o.Q, o.CapFrac*T, level);
nx = numel(xe) - 1; ny = numel(ye) - 1; N = nx*ny;
xc = (xe(1:end-1) + xe(2:end))/2; hx = diff(xe);
yc = (ye(1:end-1) + ye(2:end))/2; hy = diff(ye);
% u-limits Ux(ix, jx, s, c) = x' - x_ix at the lower (c = 1) / upper (c = 2)
% edge of source cell jx (s = 1) or of its mirror image in x = 0 (s = 2);
% rows ix are collocation cells, columns jx source cells.
Ux = zeros(nx, nx, 2, 2);
Ux(:,:,1,1) = -xc.' + xe(1:end-1);    % implicit expansion: (nx x 1) + (1 x nx)
Ux(:,:,1,2) = -xc.' + xe(2:end);
Ux(:,:,2,1) = -xc.' - xe(2:end);
Ux(:,:,2,2) = -xc.' - xe(1:end-1);
Vy = zeros(ny, ny, 2, 2);
Vy(:,:,1,1) = -yc.' + ye(1:end-1);
Vy(:,:,1,2) = -yc.' + ye(2:end);
Vy(:,:,2,1) = -yc.' - ye(2:end);
Vy(:,:,2,2) = -yc.' - ye(1:end-1);
K = zeros(N, N);
sgn = [1 -1];        % corner signs: F(u2,v2) - F(u1,v2) - F(u2,v1) + F(u1,v1)
for iy = 1:ny
    rows = (iy-1)*nx + (1:nx);
    for jy = 1:ny
        cols = (jy-1)*nx + (1:nx);
        B = zeros(nx, nx);
        for sx = 1:2
            for sy = 1:2
                for cx = 1:2
                    for cy = 1:2
                        s = sgn(3-cx)*sgn(3-cy);   % c = 2 (upper) -> +1
                        B = B + s*lnRectF(Ux(:,:,sx,cx), Vy(iy,jy,sy,cy));
                    end
                end
            end
        end
        K(rows, cols) = B;
    end
end
a = kron(hy(:), hx(:));          % cell areas, index i = ix + (iy-1)*nx
M = eye(N) - 1j*kap/(2*pi)*K;
jv = M \ ones(N, 1);
Sq = sum(a .* jv);
Aq = W*T;
ratio = real(Aq / Sq);
Ltot = mu0*imag(1/(4*Sq))/kap;                 % same log reference as L0dc
L0dc = -mu0/(2*pi) * (a.' * (K*ones(N,1))) / (4*Aq^2);
info = struct('nx', nx, 'ny', ny, 'N', N, 'hs_m', hs*L0/2^(level-1), 'kappa', kap, ...
    'delta', delta, 'Ltot', Ltot, 'L0dc', L0dc, 'dL', Ltot - L0dc);
end

function e = grid1d(Lh, hs, q, hcap, level)
% edges on [0, Lh], finest (hs) at Lh, growing by q toward 0, capped at hcap
x = Lh; h = hs; pts = Lh;
while x - h > 1e-12*Lh
    x = x - h; pts(end+1) = x; %#ok<AGROW>
    h = min(h*q, hcap);
end
if numel(pts) >= 2 && pts(end) < 0.5*(pts(end-1) - pts(end)) ...
        && numel(pts) > 2
    pts(end) = [];               % merge a sliver next to the axis
end
pts(end+1) = 0;
e = sort(pts);
m = 2^(level-1);
if m > 1
    e = interp1(1:numel(e), e, linspace(1, numel(e), (numel(e)-1)*m + 1));
end
end
