function [p, t, nface, info] = mesh_shapes(shapes, hmax, hgrad)
% MESH_SHAPES  Linear triangle mesh of a union of polygons (first shape = domain), each shape
% with an optional target element size shapes(k).h (NaN = none). Uses decsg + generateMesh.
%   p : 2 x np node coordinates, t : 3 x nt connectivity, nface : 1 x nt face id.
if nargin < 3, hgrad = 1.3; end
ns = numel(shapes);
maxv = max(arrayfun(@(s) numel(s.x), shapes));
gd = zeros(2 + 2 * maxv, ns);
names = cell(1, ns);
for k = 1:ns
    nv = numel(shapes(k).x);
    gd(1:2 + 2 * nv, k) = [2; nv; shapes(k).x(:); shapes(k).y(:)];
    names{k} = sprintf('S%d', k);
end
nsm = char(names)';
sf = strjoin(names, '+');
[dl, bt] = decsg(gd, sf, nsm);
model = createpde();
geometryFromEdges(model, dl);
nf = model.Geometry.NumFaces;
% Face sizes: a face takes the smallest h of the shapes that contain it.
hf = inf(1, nf);
for f = 1:nf
    for k = 1:ns
        if bt(f, k) && ~isnan(shapes(k).h)
            hf(f) = min(hf(f), shapes(k).h);
        end
    end
end
faces = find(isfinite(hf));
args = {'Hmax', hmax, 'Hgrad', hgrad, 'GeometricOrder', 'linear'};
if ~isempty(faces)
    % group faces by size for Hface
    hs = unique(hf(faces));
    hc = {};
    for q = 1:numel(hs)
        hc = [hc, {faces(hf(faces) == hs(q)), hs(q)}]; %#ok<AGROW>
    end
    args = [args, {'Hface', hc}];
end
generateMesh(model, args{:});
[p, ~, tt] = meshToPet(model.Mesh);
t = tt(1:3, :);
nface = tt(4, :);
info.bt = bt;
info.nf = nf;
info.hf = hf;
info.model = model;
end
