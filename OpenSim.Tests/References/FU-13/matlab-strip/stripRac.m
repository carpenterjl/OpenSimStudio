function [ratio, Lint, info] = stripRac(w, t, sigma, f, hmaxFactor, varargin)
%STRIPRAC AC/DC resistance ratio of an isolated straight conductor (2-D FEM).
%   [ratio, Lint, info] = stripRac(w, t, sigma, f, hmaxFactor) solves the
%   per-unit-length, voltage-driven eddy-current problem for an isolated
%   rectangular conductor of width w and thickness t (metres), conductivity
%   sigma (S/m), at frequency f (Hz), with the MATLAB PDE Toolbox general
%   scalar PDE (createpde(1)), A = A_z, mu = mu0 everywhere:
%       conductor:  -div((1/mu0) grad A) + j*omega*sigma*A = sigma*E0
%       air:        -div((1/mu0) grad A)                   = 0
%       A = 0 on a far circle of radius RfarFactor * (largest dimension).
%   Then J = sigma*(E0 - j*omega*A), I = int_cond J dS,
%   R_ac = Re(E0/I), R_dc = 1/(sigma*w*t), ratio = R_ac/R_dc.
%
%   The problem is solved in dimensionless form: lengths in units of
%   L0 = min(w,t)/2, u = A/(mu0*sigma*E0*L0^2), kappa = omega*mu0*sigma*L0^2:
%       -lap u + 1j*kappa*u = 1 (conductor),  -lap u = 0 (air),
%   so J/(sigma*E0) = 1 - 1j*kappa*u and ratio = Re(Area/S),
%   S = int_cond (1 - 1j*kappa*u) dS (all in units of L0).
%
%   hmaxFactor scales every conductor mesh size: the conductor (or, when a
%   core fits, the surface layer of SkinDepths skin depths) gets
%   Hface = hmaxFactor*min(delta/5, min(w,t)/10); the core gets
%   hmaxFactor*min(w,t)/10 (never finer than the layer); each outer corner
%   gets Hvertex = CornerFactor*(layer size).  Halving hmaxFactor halves all.
%
%   Lint is the internal inductance per length from the magnetic energy
%   inside the conductor, Lint = int_cond |B|^2/mu0 dS / |I|^2 (H/m).  For a
%   round wire this equals Im(Z_int)/omega exactly; for a rectangle it is
%   the "energy inside the conductor" definition.  info.dL is the
%   definition-free change of the total inductance from DC, L(f) - L(0),
%   with the same far boundary (independent of the far radius).
%
%   Name-value options:
%     'Shape'        'rect' (default) or 'round' (w = t = diameter)
%     'RfarFactor'   far radius / largest dimension (default 20; the
%                    largest dimension of a rectangle is its diagonal)
%     'Quarter'      true (default): quarter model with symmetry (zero
%                    Neumann) on x = 0 and y = 0; false: full cross-section
%     'SkinDepths'   thickness of the fine surface layer in skin depths (5)
%     'Hgrad'        mesh growth rate at hmaxFactor = 1 (default 1.3)
%     'ScaleHgrad'   true (default): growth rate 1 + (Hgrad-1)*hmaxFactor
%                    (capped at 1.6) and air Hmax = hmaxFactor*Rfar/10, so
%                    the air mesh refines with the level too; false: fixed
%                    Hgrad and Hmax = Rfar/10
%     'CornerFactor' corner vertex size / layer size (default 0.25)
%     'DcSolve'      also solve the DC problem on the same mesh for dL (true)
%     'Buffers'      true (default, rectangles): nested buffer rectangles with
%                    margins min(w,t)/2*2^k up to BufferReach*max(w,t)/2;
%                    each ring gets Hface = hs + (Hgrad_level - 1)*margin, the
%                    graded size at its outer edge, so the mesher cannot leave
%                    oversized elements near the conductor
%     'BufferReach'  outermost margin / (max(w,t)/2) (default 4)
%     'ReturnModel'  also return info.model, info.u, info.eCond (diagnostics)
%     'MeshOnly'     build the mesh only (ratio = Lint = NaN; info.model)
%
%   Example (1 mm x 35 um copper at t/delta = 4, mesh level 3):
%     mu0 = 4e-7*pi; s = 5.8e7; t = 35e-6; f = 4^2/(pi*mu0*s*t^2);
%     [r, Li, info] = stripRac(1e-3, t, s, f, 1/4);
%   Mesh level L <-> hmaxFactor = 2^(1-L).  Run three or more levels and
%   look at the change between the two finest (see run_jobs.m, summarize.m).
%
%   info: delta, kappa, nodes, elements, sizes, ratioPower (P-based form,
%   R = int|J|^2/sigma / |I|^2 for peak phasors), Z (per-length impedance
%   including the external part up to the far circle), Ltot, L0dc, dL,
%   areaRelErr, timing.

p = inputParser;
addParameter(p, 'Shape', 'rect');
addParameter(p, 'RfarFactor', 20);
addParameter(p, 'Quarter', true);
addParameter(p, 'SkinDepths', 5);
addParameter(p, 'Hgrad', 1.3);
addParameter(p, 'CornerFactor', 0.25);
addParameter(p, 'DcSolve', true);
addParameter(p, 'ScaleHgrad', true);
addParameter(p, 'ReturnModel', false);
addParameter(p, 'MeshOnly', false);
addParameter(p, 'Buffers', true);
addParameter(p, 'BufferReach', 4);
parse(p, varargin{:});
o = p.Results;

mu0 = 4e-7*pi;
omega = 2*pi*f;
delta = sqrt(2/(omega*mu0*sigma));
isRound = strcmpi(o.Shape, 'round');
if isRound
    L0 = w/2;                       % radius
    W = 1; T = 1;
    Dmax = w;
    areaExact = pi;                 % in units of L0^2 (full section)
else
    L0 = min(w, t)/2;
    W = (w/2)/L0; T = (t/2)/L0;     % half-dimensions in units of L0
    Dmax = hypot(w, t);
    areaExact = 4*W*T;
end
kap = omega*mu0*sigma*L0^2;         % = 2*(L0/delta)^2
dS = delta/L0;
Rfar = o.RfarFactor*Dmax/L0;
h0 = min(dS/5, 0.2*min(W, T));
hs = hmaxFactor*h0;                 % surface layer / whole conductor
hc = max(hs, hmaxFactor*0.2*min(W, T));
hv = o.CornerFactor*hs;
d = o.SkinDepths*dS;
if isRound
    hasCore = (1 - d) > 2*hc;
else
    hasCore = (W - d) > 2*hc && (T - d) > 2*hc;
end

% ---------------- geometry ----------------
C1 = [1; 0; 0; Rfar; zeros(6,1)];
if o.Quarter
    SQ = [3; 4; 0; Rfar; Rfar; 0; 0; 0; Rfar; Rfar];
end
if isRound
    C2 = [1; 0; 0; 1; zeros(6,1)];
    if hasCore
        C3 = [1; 0; 0; 1-d; zeros(6,1)];
        gd = [C1 C2 C3]; ns = char('C1','C2','C3'); sf = 'C1+C2+C3';
    else
        gd = [C1 C2]; ns = char('C1','C2'); sf = 'C1+C2';
    end
    if o.Quarter
        gd = [gd SQ]; ns = char(ns, 'SQ'); sf = ['(' sf ')*SQ'];
    end
else
    if o.Quarter
        R1 = [3; 4; 0; W; W; 0; 0; 0; T; T];
        R2 = [3; 4; 0; W-d; W-d; 0; 0; 0; T-d; T-d];
        gd = [C1 SQ R1]; ns = char('C1','SQ','R1'); sf = '(C1*SQ)+R1';
    else
        R1 = [3; 4; -W; W; W; -W; -T; -T; T; T];
        R2 = [3; 4; -(W-d); W-d; W-d; -(W-d); -(T-d); -(T-d); T-d; T-d];
        gd = [C1 R1]; ns = char('C1','R1'); sf = 'C1+R1';
    end
    if hasCore
        gd = [gd R2]; ns = char(ns, 'R2'); sf = [sf '+R2'];
    end
    % buffer rectangles around the conductor (margins m_k = min(W,T)*2^k
    % up to BufferReach*max(W,T)); each ring between two of them gets an
    % Hface cap equal to the intended graded size at its outer margin.
    if o.Buffers
        mk = min(W, T) * 2.^(0:20);
        mk = mk(1:find(mk >= o.BufferReach*max(W, T), 1));
        assert(numel(mk) <= 10, 'too many buffer rings');
        for k = 1:numel(mk)
            if o.Quarter
                Bk = [3; 4; 0; W+mk(k); W+mk(k); 0; 0; 0; T+mk(k); T+mk(k)];
            else
                Bk = [3; 4; -(W+mk(k)); W+mk(k); W+mk(k); -(W+mk(k)); -(T+mk(k)); -(T+mk(k)); T+mk(k); T+mk(k)];
            end
            nm = sprintf('B%d', k-1);
            gd = [gd Bk]; ns = char(ns, nm); sf = [sf '+' nm]; %#ok<AGROW>
        end
    end
end
dl = decsg(gd, sf, ns');
model = createpde(1);
g = geometryFromEdges(model, dl);

% faces by containment
if o.Quarter
    pAir = 0.5*Rfar*[cosd(45) sind(45)];
else
    pAir = [0 0.5*Rfar];
end
fAir = nearestFace(g, pAir);
if isRound
    e = 0.5*min(d, 1);
    pSkin = (1 - e)*[cosd(45) sind(45)];
    pCore = 0.5*(1 - d)*[cosd(45) sind(45)];
else
    e = 0.5*min([d, W, T]);
    pSkin = [W - e, T - e];
    pCore = 0.5*[W - d, T - d];
end
if ~o.Quarter, pCore = [0 0]; end
fSkin = nearestFace(g, pSkin);
if hasCore
    fCore = nearestFace(g, pCore);
    fCond = [fSkin fCore];
else
    fCore = [];
    fCond = fSkin;
end
fRing = []; hRing = [];
if ~isRound && o.Buffers
    mprev = 0;
    for k = 1:numel(mk)
        fRing(k) = nearestFace(g, [W + 0.5*(mprev + mk(k)), 0.5*T]); %#ok<AGROW>
        mprev = mk(k);
    end
end
assert(numel(unique([fAir fCond fRing])) == g.NumFaces, 'face identification failed');

% far-circle edges
farE = [];
for k = 1:size(dl, 2)
    if dl(1, k) == 1 && abs(hypot(dl(2,k), dl(4,k)) - Rfar) < 1e-9*Rfar ...
            && abs(hypot(dl(3,k), dl(5,k)) - Rfar) < 1e-9*Rfar
        a1 = atan2(dl(4,k), dl(2,k)); a2 = atan2(dl(5,k), dl(3,k));
        am = a1 + mod(a2 - a1, 2*pi)/2;
        farE(end+1) = nearestEdge(g, Rfar*[cos(am) sin(am)]); %#ok<AGROW>
    end
end
farE = unique(farE);
assert(~isempty(farE), 'far boundary not found');

% corner vertices of the rectangle
cornerV = [];
if ~isRound
    V = g.Vertices;
    if o.Quarter
        targets = [W T];
    else
        targets = [W T; -W T; -W -T; W -T];
    end
    for k = 1:size(targets, 1)
        [dv, iv] = min(hypot(V(:,1) - targets(k,1), V(:,2) - targets(k,2)));
        assert(dv < 1e-9*max(W, T), 'corner vertex not found');
        cornerV(end+1) = iv; %#ok<AGROW>
    end
end

% ---------------- coefficients, BC, mesh ----------------
specifyCoefficients(model, 'm', 0, 'd', 0, 'c', 1, 'a', 0, 'f', 0, 'Face', [fAir fRing]);
specifyCoefficients(model, 'm', 0, 'd', 0, 'c', 1, 'a', 1j*kap, 'f', 1, 'Face', fCond);
applyBoundaryCondition(model, 'dirichlet', 'Edge', farE, 'u', 0);

hface = {fSkin, hs};
if hasCore, hface = [hface, {fCore, hc}]; end
% The air mesh is refined with the level as well: the growth rate and the
% largest size scale with hmaxFactor, so the whole size field (not only
% the conductor) halves from one level to the next.  (A fixed Hgrad leaves
% the air mesh a fixed distance from the conductor unchanged, and for a
% non-circular section that error in the exterior field reaches R_ac at
% the 1e-4 level.)
if o.ScaleHgrad
    hgrad = min(1 + (o.Hgrad - 1)*hmaxFactor, 1.6);
    hmaxAir = min(hmaxFactor, 2)*Rfar/10;
else
    hgrad = o.Hgrad;
    hmaxAir = Rfar/10;
end
% Ring caps: the graded size h(d) = hs + (hgrad - 1)*d at the ring's outer
% margin.  Without them the 2-D mesher occasionally leaves elements 10x
% larger than this a few thicknesses from a thin strip (found with an
% edge-jump error indicator, exp16), which moved R_ac/R_dc by up to 1.6e-4.
for k = 1:numel(fRing)
    hRing(k) = hs + (hgrad - 1)*mk(k); %#ok<AGROW>
    hface = [hface, {fRing(k), hRing(k)}]; %#ok<AGROW>
end
args = {'GeometricOrder', 'quadratic', 'Hmax', hmaxAir, 'Hgrad', hgrad, 'Hface', hface};
if ~isempty(cornerV), args = [args, {'Hvertex', {cornerV, hv}}]; end
tm = tic;
generateMesh(model, args{:});
tMesh = toc(tm);
msh = model.Mesh;
if o.MeshOnly
    ratio = NaN; Lint = NaN;
    info = struct('model', model, 'L0', L0, 'kappa', kap, 'delta', delta, 'hs_m', hs*L0, ...
        'fCond', fCond, 'fAir', fAir, 'W', W, 'T', T, 'Rfar', Rfar, 'tMesh', tMesh);
    return;
end

% ---------------- solve AC ----------------
ts = tic;
res = solvepde(model);
tSolve = toc(ts);
u = res.NodalSolution;

eCond = findElements(msh, 'region', 'Face', fCond);
eAll = 1:size(msh.Elements, 2);
Qc = acQuad(msh, eCond, u, kap);
Qa = acQuad(msh, eAll, u, kap);
m = 1; if o.Quarter, m = 4; end
Afe = m*Qc.area;
S = m*(Qc.area - 1j*kap*Qc.Iu);              % I/(sigma*E0*L0^2)
ratio = real(areaExact / S);
ratioFeArea = real(Afe / S);
ratioPower = areaExact * m*Qc.Ip / abs(S)^2;  % = (int|J|^2/sigma)/|I|^2 / R_dc
Lint = mu0 * m*Qc.Ig / abs(S)^2;
Ltot = mu0 * imag(1/S) / kap;                % Im(E0/I)/omega
LtotEnergy = mu0 * m*Qa.Ig / abs(S)^2;
Z = 1/(sigma*L0^2*S);                        % ohm/m, incl. external part to Rfar

% ---------------- DC solve on the same mesh ----------------
L0dc = NaN; dL = NaN; tDc = 0;
if o.DcSolve
    specifyCoefficients(model, 'm', 0, 'd', 0, 'c', 1, 'a', 0, 'f', 1, 'Face', fCond);
    td = tic;
    r0 = solvepde(model);
    tDc = toc(td);
    Q0 = acQuad(msh, eCond, r0.NodalSolution, 0);
    L0dc = mu0 * m*Q0.Iu / (m*Q0.area)^2;
    dL = Ltot - L0dc;
end

info = struct('w', w, 't', t, 'sigma', sigma, 'f', f, 'hmaxFactor', hmaxFactor, ...
    'shape', o.Shape, 'quarter', o.Quarter, 'delta', delta, 'kappa', kap, 'L0', L0, ...
    'nodes', size(msh.Nodes, 2), 'elements', size(msh.Elements, 2), ...
    'condElements', numel(eCond), 'hs_m', hs*L0, 'hc_m', hc*L0, 'hv_m', hv*L0, ...
    'hasCore', hasCore, 'Rfar_m', Rfar*L0, 'RfarFactor', o.RfarFactor, ...
    'hgrad', hgrad, 'hmaxAir_m', hmaxAir*L0, 'nRings', numel(fRing), ...
    'ratio', ratio, 'ratioFeArea', ratioFeArea, 'ratioPower', ratioPower, ...
    'areaRelErr', Afe/areaExact - 1, 'Lint', Lint, 'Ltot', Ltot, ...
    'LtotEnergy', LtotEnergy, 'L0dc', L0dc, 'dL', dL, 'Z', Z, ...
    'tMesh', tMesh, 'tSolve', tSolve, 'tDc', tDc);
if o.ReturnModel
    info.model = model; info.u = u; info.eCond = eCond;
end
end
