function [pcb, g] = build_filter(o)
% build_filter  Sheen (1991) low-pass filter, Fig. 3.25 (thesis p.108 = PDF 108),
% as an RF PCB Toolbox pcbComponent (MoM).  All lengths in mm in g, metres in pcb.
%
% Geometry (geometry_verified.json, Fig. 3.25 p.108):
%   long patch 20.32 (x) x 2.54 (y); two 2.413 mm port lines along y on opposite
%   long sides; near edge of each line 5.65 mm from the nearer patch end
%   -> line centre 5.65 + 2.413/2 = 6.8565 mm from the patch end.
%   Port 1 near the -x end on the -y side, port 2 near the +x end on the +y side (Z layout).
%   Substrate er = 2.2, h = 0.794 mm, no loss tangent given -> lossless.
%   PEC zero-thickness metal (FD-TD model p.76; no conductor data given).
% Ports: edge feeds at the board edge, which is placed at the thesis FD-TD
%   reference planes, 10*dy = 10*0.4232 = 4.232 mm from the patch edges (p.112),
%   unless o.Lfeed overrides it (feed-length sensitivity runs).
% Board/ground size: NOT given in the thesis.  x-extent = patch + 2*o.gx.
%
% o fields (all optional): Lpatch, Wpatch, Wline, dEnd, Lfeed, gx, er, h
if nargin < 1, o = struct(); end
d = struct('Lpatch',20.32,'Wpatch',2.54,'Wline',2.413,'dEnd',5.65, ...
           'Lfeed',4.232,'gx',10.0,'er',2.2,'h',0.794);
fn = fieldnames(d);
for k = 1:numel(fn), if ~isfield(o,fn{k}), o.(fn{k}) = d.(fn{k}); end, end
g = o;
mm = 1e-3;
xc = o.Lpatch/2 - o.dEnd - o.Wline/2;         % |x| of each port centreline (3.3035 for the drawing)
g.xport = xc;
yEdge = o.Wpatch/2 + o.Lfeed;                 % board edge = port plane
g.yEdge = yEdge;
ov = 0.05;                                    % 0.05 mm overlap of line into patch (same union shape)
Lline = o.Lfeed + ov;
patch = traceRectangular('Length',o.Lpatch*mm,'Width',o.Wpatch*mm,'Center',[0 0]);
l1 = traceRectangular('Length',o.Wline*mm,'Width',Lline*mm, ...
      'Center',[-xc, -(o.Wpatch/2 + o.Lfeed/2 - ov/2)]*mm);
l2 = traceRectangular('Length',o.Wline*mm,'Width',Lline*mm, ...
      'Center',[ xc,  (o.Wpatch/2 + o.Lfeed/2 - ov/2)]*mm);
top = patch + l1 + l2;
BL = o.Lpatch + 2*o.gx;  BW = 2*yEdge;
g.BL = BL; g.BW = BW;
gnd = traceRectangular('Length',BL*mm,'Width',BW*mm,'Center',[0 0]);
sub = dielectric('Name','sub','EpsilonR',o.er,'LossTangent',0,'Thickness',o.h*mm);
pcb = pcbComponent;
pcb.Name = 'Sheen LPF';
pcb.BoardShape = traceRectangular('Length',BL*mm,'Width',BW*mm,'Center',[0 0]);
pcb.BoardThickness = o.h*mm;
pcb.Layers = {top, sub, gnd};
pcb.FeedLocations = [-xc*mm, -yEdge*mm, 1, 3;  xc*mm, yEdge*mm, 1, 3];
pcb.FeedDiameter = o.Wline/2*mm;              % catalog convention: FeedDiameter = trace width/2
pcb.Conductor = metal('PEC');
end
