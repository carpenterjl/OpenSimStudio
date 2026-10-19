function R = mm_trirule(n)
%MM_TRIRULE Quadrature on the reference triangle {xi>=0, eta>=0, xi+eta<=1}
% by the Duffy-collapsed n x n Gauss-Legendre product rule (computed here by
% Golub-Welsch, no tabulated constants). Exact for polynomials of degree
% <= 2n-2 in (xi, eta). Weights sum to 1/2 (the reference area).
if nargin < 1, n = 4; end
k = 1:n-1;
beta = k ./ sqrt(4*k.^2 - 1);
[V, D] = eig(diag(beta,1) + diag(beta,-1));
x = diag(D); w = 2 * V(1,:)'.^2;
[x, i] = sort(x); w = w(i);
g = (x + 1) / 2; wg = w / 2;                 % Gauss-Legendre on [0,1]
[S, T] = ndgrid(g, g); [WS, WT] = ndgrid(wg, wg);
R.xi  = S(:);
R.eta = T(:) .* (1 - S(:));
R.w   = WS(:) .* WT(:) .* (1 - S(:));
% self-test: int over reference triangle of xi^a eta^b = a! b! / (a+b+2)!
worst = 0;
for a = 0:2*n-2
  for b = 0:2*n-2-a
    exact = factorial(a) * factorial(b) / factorial(a + b + 2);
    got = sum(R.w .* R.xi.^a .* R.eta.^b);
    worst = max(worst, abs(got - exact) / exact);
  end
end
if worst > 1e-12, error('mm_trirule: self-test failed (%g)', worst); end
R.selftest = worst;
end
