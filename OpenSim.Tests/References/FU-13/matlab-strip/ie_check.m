% ie_check.m - checks of the integral-equation cross-check code
syms u v real
Fs = (u*v*log(u^2+v^2) - 3*u*v + u^2*atan(v/u) + v^2*atan(u/v))/2;
d = simplify(diff(diff(Fs, u), v) - log(u^2+v^2)/2);
fprintf('symbolic d2F/dudv - ln(u^2+v^2)/2 = %s\n', char(d));
% closed-form cell integrals vs integral2
cellInt = @(x1,x2,y1,y2,x0,y0) lnRectF(x2-x0,y2-y0) - lnRectF(x1-x0,y2-y0) - lnRectF(x2-x0,y1-y0) + lnRectF(x1-x0,y1-y0);
g = @(x,y,x0,y0) log(hypot(x-x0, y-y0));
cfg = [0 1 0 0.5 3 2;  0 1 0 0.5 0.5 0.25;  0 1 0 0.5 1 0.5;  0 1 0 0.5 0 0.1; 2 2.1 -1 1 2.05 0.3];
for k = 1:size(cfg,1)
    c = num2cell(cfg(k,:)); [x1,x2,y1,y2,x0,y0] = c{:};
    cf = cellInt(x1,x2,y1,y2,x0,y0);
    % split at the point so integral2 sees the log singularity only at corners
    xs = unique([x1, min(max(x0,x1),x2), x2]); ys = unique([y1, min(max(y0,y1),y2), y2]);
    nm = 0;
    for i = 1:numel(xs)-1
        for j = 1:numel(ys)-1
            nm = nm + integral2(@(x,y) g(x,y,x0,y0), xs(i), xs(i+1), ys(j), ys(j+1), 'AbsTol', 1e-14, 'RelTol', 1e-12);
        end
    end
    fprintf('cell [%g,%g]x[%g,%g] point (%g,%g): closed %.14f  integral2 %.14f  diff %.2e\n', x1,x2,y1,y2,x0,y0, cf, nm, cf-nm);
end
% IE timing / sanity
sigma = 5.8e7; mu0 = 4e-7*pi;
w = 1e-3; t = 0.25e-3; r = 8; f = r^2/(pi*mu0*sigma*t^2);
for lev = 1:3
    tic; [ra, info] = ieRac(w, t, sigma, f, lev); tt = toc;
    fprintf('IE b t/d=8 level %d N %d (nx %d ny %d): R/Rdc %.9f dL %.6e  %.1f s\n', lev, info.N, info.nx, info.ny, ra, info.dL, tt);
end
f0 = 1; [ra, info] = ieRac(w, t, sigma, f0, 2);
fprintf('IE b at 1 Hz: R/Rdc %.12f\n', ra);
