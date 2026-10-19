function [K, M, F] = p1_assemble(g, kcoef, mcoef, jsrc)
% P1_ASSEMBLE  K_ab = kcoef*A*grad(Na).grad(Nb); M_ab = mcoef*A/12*(1+delta_ab); F_a = jsrc*A/3.
% kcoef, mcoef, jsrc are 1 x nt per-element values.
t = g.t; nt = size(t, 2); np = g.np;
I = zeros(9, nt); Jx = zeros(9, nt); Kv = zeros(9, nt); Mv = zeros(9, nt);
q = 0;
for a = 1:3
    for b = 1:3
        q = q + 1;
        I(q, :) = t(a, :); Jx(q, :) = t(b, :);
        Kv(q, :) = kcoef .* g.area .* (g.bx(a, :) .* g.bx(b, :) + g.by(a, :) .* g.by(b, :));
        Mv(q, :) = mcoef .* g.area / 12 * (1 + (a == b));
    end
end
K = sparse(I(:), Jx(:), Kv(:), np, np);
M = sparse(I(:), Jx(:), Mv(:), np, np);
F = accumarray(reshape(t, [], 1), reshape(repmat(jsrc .* g.area / 3, 3, 1), [], 1), [np, 1]);
end
