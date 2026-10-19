function [xi, eta, w] = triRule(n)
%TRIRULE Collapsed Gauss-Legendre rule on the reference triangle
%   {xi >= 0, eta >= 0, xi + eta <= 1} (area 1/2): xi = s, eta = (1-s) t,
%   weight ws*wt*(1-s).  Gauss-Legendre nodes/weights by Golub-Welsch.
k = 1:n-1;
b = k ./ sqrt(4*k.^2 - 1);
[V, D] = eig(diag(b, 1) + diag(b, -1));
x = (diag(D) + 1) / 2;            % nodes on [0,1]
g = (2 * V(1, :).'.^2) / 2;       % weights on [0,1]
[S, T] = ndgrid(x, x);
[WS, WT] = ndgrid(g, g);
xi = S(:);
eta = (1 - S(:)) .* T(:);
w = WS(:) .* WT(:) .* (1 - S(:));
end
