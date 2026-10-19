function F = lnRectF(u, v)
%LNRECTF Antiderivative F(u,v) with d2F/(du dv) = (1/2) ln(u^2 + v^2),
%   F = (1/2)[u v ln(u^2+v^2) - 3 u v + u^2 atan(v/u) + v^2 atan(u/v)],
%   continuous at u = 0 or v = 0 (limits taken).  The integral of
%   ln sqrt(u^2+v^2) over [u1,u2]x[v1,v2] is
%   F(u2,v2) - F(u1,v2) - F(u2,v1) + F(u1,v1).  (Checked symbolically and
%   against integral2 in ie_check.m.)  u and v may be any compatible sizes.
z = zeros(size(u + v));
u = u + z; v = v + z;
r2 = u.^2 + v.^2;
t1 = u.*v.*log(r2);       t1(r2 == 0) = 0;
t3 = u.^2 .* atan(v./u);  t3(u == 0) = 0;
t4 = v.^2 .* atan(u./v);  t4(v == 0) = 0;
F = 0.5*(t1 - 3*u.*v + t3 + t4);
end
