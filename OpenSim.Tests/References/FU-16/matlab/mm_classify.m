function [faceRegion, edgeSide] = mm_classify(msh, nFaces, nEdges, regions, domain)
%MM_CLASSIFY faceRegion(f) = index of the region owning face f (0 = background),
% using an interior point of the face (centroid of its first element) and
% OpenSim's precedence (the last region containing the point wins).
% edgeSide(k) = 'L','R','B','T' for outer-boundary edges, ' ' otherwise.
faceRegion = zeros(1, nFaces);
for f = 1:nFaces
  el = findElements(msh, "region", Face=f);
  c = mean(msh.Nodes(:, msh.Elements(1:3, el(1))), 2);
  owner = 0;
  for k = numel(regions):-1:1
    r = regions(k);
    if c(1) > r.x0 && c(1) < r.x1 && c(2) > r.y0 && c(2) < r.y1, owner = k; break; end
  end
  faceRegion(f) = owner;
end
span = max(domain(2) - domain(1), domain(4) - domain(3));
tol = 1e-9 * span;
edgeSide = repmat(' ', 1, nEdges);
for k = 1:nEdges
  nd = findNodes(msh, "region", Edge=k);
  x = msh.Nodes(1, nd); y = msh.Nodes(2, nd);
  if all(abs(x - domain(1)) < tol), edgeSide(k) = 'L';
  elseif all(abs(x - domain(2)) < tol), edgeSide(k) = 'R';
  elseif all(abs(y - domain(3)) < tol), edgeSide(k) = 'B';
  elseif all(abs(y - domain(4)) < tol), edgeSide(k) = 'T';
  end
end
end
