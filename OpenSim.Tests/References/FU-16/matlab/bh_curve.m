function C = bh_curve(variant)
%BH_CURVE The gate's analytic B-H curve, tabulated and interpolated as H(B).
%  B(H) = mu0*H + (2*Js/pi)*atan(pi*(mur_i - 1)*mu0*H/(2*Js)), Js = 1.6 T,
%  mur_i = 2000, mu0 = 4e-7*pi, tabulated at H = 0 and H_k = 10^(5k/80) A/m,
%  k = 0..80 (82 points).
% variant 'pchip' (default): MATLAB's monotone shape-preserving pchip of H(B).
% variant 'opensim': the Fritsch-Carlson slopes OpenSim's BhCurve uses
%  (OpenSim.Solvers/Magnetics/MagneticMaterial.cs lines 17-41: interior slope
%  = harmonic mean 2/(1/s0 + 1/s1) of the neighbouring secants, 0 at a sign
%  change; end slopes = end secants), on the same table.
% Both: above the last point the saturated tail H = H_n + (B - B_n)/mu0
% (MagneticMaterial.cs lines 52-53), so w(B) = int_0^B H dB continues there.
% Returns C.H(B), C.dH(B), C.w(B) (energy density int_0^B H dB, exact integral
% of the cubic pieces), C.nu(B) = H/B, C.mur(B) = B/(mu0 H), C.table.
if nargin < 1, variant = 'pchip'; end
mu0 = 4e-7*pi; Js = 1.6; muri = 2000;
Hk = [0, 10.^(5*(0:80)/80)];
Bk = mu0*Hk + (2*Js/pi)*atan(pi*(muri - 1)*mu0*Hk/(2*Js));
C.table = [Hk(:), Bk(:)];
C.mu0 = mu0;
switch variant
  case 'pchip'
    pp = pchip(Bk, Hk);
  case 'opensim'
    n = numel(Bk);
    sec = diff(Hk) ./ diff(Bk);
    s = zeros(1, n);
    s(1) = sec(1); s(n) = sec(n-1);
    for i = 2:n-1
      if sec(i-1)*sec(i) <= 0, s(i) = 0; else, s(i) = 2/(1/sec(i-1) + 1/sec(i)); end
    end
    pp = mkpp_hermite(Bk, Hk, s);
  otherwise
    error('bh_curve: unknown variant %s', variant);
end
C.variant = variant;
C.pp = pp;
[brk, coefs, L, ord] = unmkpp(pp);
dcoefs = coefs(:, 1:ord-1) .* (ord-1:-1:1);
C.dpp = mkpp(brk, dcoefs);
% cumulative integral at the breaks: int of a*t^3 + b*t^2 + c*t + d
hseg = diff(brk);
icoefs = [coefs ./ (ord:-1:1), zeros(L, 1)];      % antiderivative coefficients (zero at the break)
segInt = polyvalrows(icoefs, hseg(:));
wk = [0; cumsum(segInt)];
C.wk = wk;
Bn = brk(end); Hn = Hk(end); wn = wk(end);
C.Bmax = Bn; C.Hmax = Hn;
C.H = @(b) Hfun(abs(b));
C.dH = @(b) dHfun(abs(b));
C.w = @(b) wfun(abs(b));
C.nu0 = ppval(C.dpp, 0);
C.nu = @(b) nufun(abs(b));
C.mur = @(b) 1 ./ (mu0 * C.nu(b));

  function h = Hfun(b)
    h = ppval(pp, min(b, Bn));
    hi = b > Bn; h(hi) = Hn + (b(hi) - Bn)/mu0;
  end
  function d = dHfun(b)
    d = ppval(C.dpp, min(b, Bn));
    d(b > Bn) = 1/mu0;
  end
  function v = wfun(b)
    bb = min(b, Bn);
    idx = discretize(bb, brk); idx(isnan(idx)) = L;
    t = bb - brk(idx);
    v = wk(idx(:)) + polyvalrows(icoefs(idx(:), :), t(:));
    v = reshape(v, size(b));
    hi = b > Bn; db = b(hi) - Bn;
    v(hi) = wn + Hn*db + 0.5*db.^2/mu0;
  end
  function v = nufun(b)
    v = Hfun(b) ./ b;
    small = b < 1e-9*Bn;
    v(small) = C.nu0;
  end
end

function y = polyvalrows(c, t)
% each row of c is a polynomial (highest power first) evaluated at t(row)
y = zeros(size(t));
for k = 1:size(c, 2), y = y .* t + c(:, k); end
end

function pp = mkpp_hermite(x, y, s)
h = diff(x); dy = diff(y);
s0 = s(1:end-1); s1 = s(2:end);
c3 = (s0 + s1 - 2*dy./h) ./ h.^2;
c2 = (3*dy./h - 2*s0 - s1) ./ h;
pp = mkpp(x, [c3(:), c2(:), s0(:), y(1:end-1).']);
end
