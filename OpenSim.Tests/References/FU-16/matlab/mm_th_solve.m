function U = mm_th_solve(S, omega, F)
%MM_TH_SOLVE Solve (K + j*omega*A) u = F for every column of F, psi = 0 on the
% Dirichlet nodes (outer box and the axis).
Kw = S.K + 1i*omega*S.A;
U = zeros(size(F));
fr = S.free;
U(fr, :) = Kw(fr, fr) \ F(fr, :);
end
