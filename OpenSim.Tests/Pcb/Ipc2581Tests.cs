using System.Text;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Ipc2581;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class Ipc2581Tests
{
    // ---------------- fixture helpers ----------------

    /// <summary>Wraps CadData content in a minimal valid IPC-2581 revision B document.</summary>
    private static string Document(string layers, string stackup, string step,
        string units = "MILLIMETER", string dictionary = "")
    {
        return $"""
            <?xml version="1.0"?>
            <IPC-2581 revision="B" xmlns="http://webstds.ipc.org/2581">
              <Content roleRef="Owner">
                <DictionaryStandard units="{units}">{dictionary}</DictionaryStandard>
              </Content>
              <Ecad name="design">
                <CadHeader units="{units}" />
                <CadData>
                  {layers}
                  <Stackup name="Stackup">
                    <StackupGroup name="All" thickness="1">{stackup}</StackupGroup>
                  </Stackup>
                  <Step name="board">
                    {step}
                  </Step>
                </CadData>
              </Ecad>
            </IPC-2581>
            """;
    }

    private const string TwoLayerDecls = """
        <Layer name="Top Layer" layerFunction="SIGNAL" side="TOP" polarity="POSITIVE" />
        <Layer name="Core" layerFunction="DIELCORE" side="NONE" polarity="POSITIVE" />
        <Layer name="Bottom Layer" layerFunction="SIGNAL" side="BOTTOM" polarity="POSITIVE" />
        """;

    private const string TwoLayerStackup = """
        <StackupLayer layerOrGroupRef="Top Layer" thickness="0.035" sequence="1" />
        <StackupLayer layerOrGroupRef="Core" thickness="1.6" sequence="2" />
        <StackupLayer layerOrGroupRef="Bottom Layer" thickness="0.035" sequence="3" />
        """;

    private const string RectProfile = """
        <Profile>
          <Polygon>
            <PolyBegin x="0" y="0" />
            <PolyStepSegment x="20" y="0" />
            <PolyStepSegment x="20" y="10" />
            <PolyStepSegment x="0" y="10" />
            <PolyStepSegment x="0" y="0" />
          </Polygon>
        </Profile>
        """;

    private static Ipc2581Board Parse(string xml) =>
        new Ipc2581Parser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    private static OpenSim.Pcb.Import.PcbBoard Read(string xml) =>
        new Ipc2581Reader().Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    // ---------------- units & profile ----------------

    [Fact]
    public void MillimeterUnits_ConvertToMeters()
    {
        var board = Parse(Document(TwoLayerDecls, TwoLayerStackup, RectProfile));
        var outer = board.Profile[0].Outer;
        Assert.Contains(outer, p => Math.Abs(p.X - 20e-3) < 1e-12 && Math.Abs(p.Y - 10e-3) < 1e-12);
        Assert.Equal(20e-3 * 10e-3, board.Profile[0].Area(), 1e-9);
    }

    [Fact]
    public void InchUnits_ScaleBy254()
    {
        var xml = Document(TwoLayerDecls, TwoLayerStackup, """
            <Profile>
              <Polygon>
                <PolyBegin x="0" y="0" />
                <PolyStepSegment x="1" y="0" />
                <PolyStepSegment x="1" y="1" />
                <PolyStepSegment x="0" y="1" />
              </Polygon>
            </Profile>
            """, units: "INCH");
        var board = Parse(xml);
        Assert.Equal(25.4e-3 * 25.4e-3, board.Profile[0].Area(), 1e-9);
    }

    [Fact]
    public void Profile_WithArc_MatchesAnalyticArea()
    {
        // A 10 mm square whose right edge bulges out as a semicircle of radius 5 mm.
        var xml = Document(TwoLayerDecls, TwoLayerStackup, """
            <Profile>
              <Polygon>
                <PolyBegin x="0" y="0" />
                <PolyStepSegment x="10" y="0" />
                <PolyStepCurve x="10" y="10" centerX="10" centerY="5" clockwise="false" />
                <PolyStepSegment x="0" y="10" />
                <PolyStepSegment x="0" y="0" />
              </Polygon>
            </Profile>
            """);
        var board = Parse(xml);
        double expected = 10e-3 * 10e-3 + Math.PI * 5e-3 * 5e-3 / 2;
        Assert.Equal(expected, board.Profile[0].Area(), expected * 1e-3);
    }

    [Fact]
    public void Profile_Cutout_BecomesHole()
    {
        var xml = Document(TwoLayerDecls, TwoLayerStackup, """
            <Profile>
              <Polygon>
                <PolyBegin x="0" y="0" />
                <PolyStepSegment x="20" y="0" />
                <PolyStepSegment x="20" y="10" />
                <PolyStepSegment x="0" y="10" />
              </Polygon>
              <Cutout>
                <PolyBegin x="5" y="2" />
                <PolyStepSegment x="8" y="2" />
                <PolyStepSegment x="8" y="6" />
                <PolyStepSegment x="5" y="6" />
              </Cutout>
            </Profile>
            """);
        var board = Parse(xml);
        Assert.Single(board.Profile[0].Holes);
        Assert.Equal(20e-3 * 10e-3 - 3e-3 * 4e-3, board.Profile[0].Area(), 1e-9);
    }

    [Fact]
    public void MissingProfile_ThrowsActionable()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Parse(Document(TwoLayerDecls, TwoLayerStackup, "")));
        Assert.Contains("Profile", ex.Message);
    }

    // ---------------- layer stack ----------------

    [Fact]
    public void Stackup_OrdersConductorsAndSumsGaps()
    {
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, RectProfile));
        Assert.NotNull(board.Stackup);
        Assert.Collection(board.Stackup!.CopperLayerThicknesses,
            t => Assert.Equal(0.035e-3, t, 1e-9),
            t => Assert.Equal(0.035e-3, t, 1e-9));
        Assert.Collection(board.Stackup.DielectricGapThicknesses,
            g => Assert.Equal(1.6e-3, g, 1e-9));
        Assert.Equal(2, board.Layers.Count);
        Assert.Equal("Top Layer", board.Layers.First(l => l.CopperOrder == 1).FileName);
        Assert.Equal("Bottom Layer", board.Layers.First(l => l.CopperOrder == 2).FileName);
    }

    [Fact]
    public void Stackup_SequenceWinsOverDeclarationOrder()
    {
        // Layers declared bottom-first; the stackup sequence must still make "Top Layer"
        // copper order 1 (the declaration order coincidentally matching the stack is the
        // untested trap the original fixture fell into).
        const string shuffledDecls = """
            <Layer name="Bottom Layer" layerFunction="SIGNAL" side="BOTTOM" polarity="POSITIVE" />
            <Layer name="Top Layer" layerFunction="SIGNAL" side="TOP" polarity="POSITIVE" />
            <Layer name="Core" layerFunction="DIELCORE" side="NONE" polarity="POSITIVE" />
            """;
        var board = Read(Document(shuffledDecls, TwoLayerStackup, RectProfile));
        Assert.Equal("Top Layer", board.Layers.First(l => l.CopperOrder == 1).FileName);
        Assert.Equal("Bottom Layer", board.Layers.First(l => l.CopperOrder == 2).FileName);
    }

    [Fact]
    public void Stackup_WithUnresolvedRefs_WarnsAndFallsBackToDeclarationOrder()
    {
        // A stackup that references only group names matches no declared layer — the
        // importer must degrade loudly, not silently reorder the stack.
        const string groupRefStackup = """
            <StackupLayer layerOrGroupRef="GroupA" thickness="0.035" sequence="1" />
            <StackupLayer layerOrGroupRef="GroupB" thickness="1.6" sequence="2" />
            """;
        var board = Read(Document(TwoLayerDecls, groupRefStackup, RectProfile));
        Assert.Contains(board.Warnings, w => w.Contains("match no declared"));
        // Declaration order fallback still yields both conductors.
        Assert.Equal("Top Layer", board.Layers.First(l => l.CopperOrder == 1).FileName);
        Assert.Equal("Bottom Layer", board.Layers.First(l => l.CopperOrder == 2).FileName);
    }

    // ---------------- nets & geometry ----------------

    private const string TraceStep = """
        <LayerFeature layerRef="Top Layer">
          <Set net="RS+">
            <Features>
              <Line startX="2" startY="5" endX="10" endY="5">
                <LineDesc lineEnd="ROUND" lineWidth="0.4" lineProperty="SOLID" />
              </Line>
              <Line startX="10" startY="5" endX="16" endY="5">
                <LineDesc lineEnd="ROUND" lineWidth="0.4" lineProperty="SOLID" />
              </Line>
            </Features>
          </Set>
          <Set>
            <Features>
              <Location x="3" y="8" />
              <RectCenter width="1" height="2" />
            </Features>
          </Set>
        </LayerFeature>
        """;

    [Fact]
    public void Traces_UnionIntoOneNamedIsland()
    {
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, RectProfile + TraceStep));
        var net = board.Nets.Single(n => n.Name == "RS+");
        Assert.Single(net.Islands);
        Assert.Equal(1, net.Islands[0].LayerOrder);

        // Two collinear 0.4 mm strokes with round caps union into one 14 mm capsule.
        double expected = 14e-3 * 0.4e-3 + Math.PI * 0.2e-3 * 0.2e-3;
        Assert.Equal(expected, net.Area, expected * 5e-3);
        Assert.StartsWith("RS+", net.Label);
    }

    [Fact]
    public void FeaturesWithoutNet_LandInNoNetBucket()
    {
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, RectProfile + TraceStep));
        // The RectCenter flash under the net-less Set becomes an unnamed net.
        var unnamed = board.Nets.Single(n => n.Name is null);
        Assert.Single(unnamed.Islands);
        Assert.Equal(1e-3 * 2e-3, unnamed.Area, 1e-3 * 2e-3 * 1e-2);
    }

    [Fact]
    public void ZeroWidthTrace_NotesAndSkips()
    {
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Features>
                  <Line startX="2" startY="5" endX="10" endY="5">
                    <LineDesc lineEnd="ROUND" lineWidth="0" lineProperty="SOLID" />
                  </Line>
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));
        Assert.DoesNotContain(board.Nets, n => n.Name == "X");
        // A zero-width stroke declares no copper AREA — informational, not a warning
        // (the zero-warning contract counts only skipped/approximated declared content).
        Assert.Contains(board.Notes, n => n.Contains("zero-width"));
        Assert.DoesNotContain(board.Warnings, w => w.Contains("zero-width"));
    }

    // ---------------- diagnostics severity (the zero-warning contract) ----------------

    private const string GoodTraceStep = RectProfile + """
        <LayerFeature layerRef="Top Layer">
          <Set net="X">
            <Features>
              <Line startX="2" startY="5" endX="10" endY="5">
                <LineDesc lineEnd="ROUND" lineWidth="0.3" lineProperty="SOLID" />
              </Line>
            </Features>
          </Set>
        </LayerFeature>
        """;

    [Fact]
    public void MissingStackup_AndDefaults_AreNotes_NotWarnings()
    {
        // No StackupLayer rows: layer order/thicknesses are genuinely absent from the
        // file, so defaults apply with NOTES — the import itself is warning-free.
        var board = Read(Document(TwoLayerDecls, "", GoodTraceStep));

        Assert.Empty(board.Warnings);
        Assert.Contains(board.Notes, n => n.Contains("no Stackup section"));
        Assert.Contains(board.Notes, n => n.Contains("no stackup thickness"));
        Assert.Contains(board.Notes, n => n.Contains("Board-build timing"));
        Assert.Contains(board.Notes, n => n.Contains("nets ("));
        Assert.Contains(board.Nets, n => n.Name == "X");
    }

    [Fact]
    public void NonstandardAttributeAndTextual_SkipSilently_SiblingsSurvive()
    {
        // Metadata elements between two traces: both must be consumed without a warning,
        // and Textual's CHILD must not leak into the feature dispatch (the subtree-skip
        // sibling-swallow class of bug).
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Features>
                  <Line startX="2" startY="5" endX="8" endY="5">
                    <LineDesc lineEnd="ROUND" lineWidth="0.3" lineProperty="SOLID" />
                  </Line>
                  <NonstandardAttribute name="PADSTACK_USAGE" value="Through_via" type="STRING" />
                  <Textual textString="REF**"><BoundingBox lowerLeftX="0" lowerLeftY="0" upperRightX="1" upperRightY="1" /></Textual>
                  <Line startX="8" startY="5" endX="14" endY="5">
                    <LineDesc lineEnd="ROUND" lineWidth="0.3" lineProperty="SOLID" />
                  </Line>
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));

        Assert.Empty(board.Warnings);
        // Both lines landed (one island 2→14) — the second sibling was not swallowed.
        var net = board.Nets.Single(n => n.Name == "X");
        Assert.Equal(2, board.TraceCenterlines.Count);
        Assert.True(net.Area > 12e-3 * 0.3e-3 * 0.99);
    }

    // ---------------- style dictionaries + Polyline + fill semantics ----------------

    private const string StyleDictionaries = """
        <DictionaryLineDesc units="MILLIMETER">
          <EntryLineDesc id="ROUND_300"><LineDesc lineEnd="ROUND" lineWidth="0.3"/></EntryLineDesc>
        </DictionaryLineDesc>
        <DictionaryFillDesc units="MILLIMETER">
          <EntryFillDesc id="SOLID_FILL"><FillDesc fillProperty="FILL"/></EntryFillDesc>
          <EntryFillDesc id="HOLLOW_FILL"><FillDesc fillProperty="HOLLOW"/></EntryFillDesc>
        </DictionaryFillDesc>
        """;

    [Fact]
    public void Polyline_WithLineDescRef_StrokesTheResolvedWidth_AndKeepsCenterlines()
    {
        // The Cadence routed-copper construct: an L-shaped open path, width via ref.
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Features>
                  <Location x="0.0" y="0.0"/>
                  <Polyline>
                    <PolyBegin x="2" y="2"/>
                    <PolyStepSegment x="10" y="2"/>
                    <PolyStepSegment x="10" y="8"/>
                    <LineDescRef id="ROUND_300"/>
                  </Polyline>
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls + StyleDictionaries, TwoLayerStackup, step));

        Assert.Empty(board.Warnings);
        var net = board.Nets.Single(n => n.Name == "X");
        // Stroked capsule area of a 14 mm path at w = 0.3 mm (round caps + convex bend
        // add ~one full circle of w/2 in total).
        double ideal = 14e-3 * 0.3e-3 + Math.PI * 0.15e-3 * 0.15e-3;
        Assert.Equal(ideal, net.Area, ideal * 2e-2);
        // Both legs land as PEEC centerlines carrying the resolved width.
        Assert.Equal(2, board.TraceCenterlines.Count);
        Assert.All(board.TraceCenterlines, c => Assert.Equal(0.3e-3, c.Width, 1e-12));
    }

    [Fact]
    public void Polyline_WithArcStep_AccumulatesChordCenterlines()
    {
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Features>
                  <Polyline>
                    <PolyBegin x="2" y="5"/>
                    <PolyStepCurve x="8" y="5" centerX="5" centerY="5" clockwise="false"/>
                    <LineDescRef id="ROUND_300"/>
                  </Polyline>
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls + StyleDictionaries, TwoLayerStackup, step));

        Assert.Empty(board.Warnings);
        // Half-circle of radius 3 mm: chords accumulate above the width/2 stub tolerance,
        // and the path's endpoints are preserved exactly.
        Assert.True(board.TraceCenterlines.Count >= 3);
        var first = board.TraceCenterlines[0];
        var last = board.TraceCenterlines[^1];
        Assert.Equal(2e-3, first.Start.X, 1e-9);
        Assert.Equal(8e-3, last.End.X, 1e-9);
    }

    [Fact]
    public void HollowRing_IsStrokedAsAnOutline_NotFilled()
    {
        // A HOLLOW square ring 8×8 mm: filling it would fabricate ~64 mm² of copper
        // (the short hazard); the correct copper is the stroked outline ring.
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Features>
                  <Polygon>
                    <PolyBegin x="2" y="1"/>
                    <PolyStepSegment x="10" y="1"/>
                    <PolyStepSegment x="10" y="9"/>
                    <PolyStepSegment x="2" y="9"/>
                    <PolyBegin x="2" y="1"/>
                    <FillDescRef id="HOLLOW_FILL"/>
                    <LineDescRef id="ROUND_300"/>
                  </Polygon>
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls + StyleDictionaries, TwoLayerStackup, step));

        Assert.Empty(board.Warnings);
        var net = board.Nets.Single(n => n.Name == "X");
        // Stroked closed outline: perimeter × width + corner caps ≈ one circle.
        double outline = 32e-3 * 0.3e-3 + Math.PI * 0.15e-3 * 0.15e-3;
        Assert.Equal(outline, net.Area, outline * 5e-2);
        Assert.True(net.Area < 64e-6 * 0.2, "a HOLLOW ring must never pour the enclosed area");
        // The enclosed region stays open: the island carries a hole.
        Assert.Contains(net.Islands, i => i.Shape.Holes.Count > 0);
    }

    [Fact]
    public void UnknownLineDescRef_WarnsAndSkipsTheDraw()
    {
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Features>
                  <Polyline>
                    <PolyBegin x="2" y="5"/>
                    <PolyStepSegment x="10" y="5"/>
                    <LineDescRef id="MISSING_STYLE"/>
                  </Polyline>
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls + StyleDictionaries, TwoLayerStackup, step));

        Assert.Contains(board.Warnings, w => w.Contains("MISSING_STYLE"));
        Assert.DoesNotContain(board.Nets, n => n.Name == "X");
        // The unresolved ref is a WARNING, not an extra zero-width note.
        Assert.DoesNotContain(board.Notes, n => n.Contains("zero-width"));
    }

    // ---------------- user dictionary, mirror, Marking, LocalFiducial ----------------

    [Fact]
    public void UserPrimitiveRef_PlacesTheDictionaryFigure()
    {
        // A user figure: an L-path stroke + a HOLLOW circle outline (the KiCad figure
        // style — bare primitives at the entry's local origin). Flashed at (10, 5).
        var dictionaries = """
            <DictionaryUser units="MILLIMETER">
              <EntryUser id="FIG_L">
                <UserSpecial>
                  <Polyline>
                    <PolyBegin x="0" y="0"/>
                    <PolyStepSegment x="3" y="0"/>
                    <LineDesc lineEnd="ROUND" lineWidth="0.2"/>
                  </Polyline>
                  <Circle diameter="2">
                    <LineDesc lineEnd="ROUND" lineWidth="0.1"/>
                    <FillDesc fillProperty="HOLLOW"/>
                  </Circle>
                </UserSpecial>
              </EntryUser>
            </DictionaryUser>
            """;
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Features>
                  <Location x="10" y="5"/>
                  <UserPrimitiveRef id="FIG_L"/>
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls + dictionaries, TwoLayerStackup, step));

        Assert.Empty(board.Warnings);
        var net = board.Nets.Single(n => n.Name == "X");
        // The stroked L-path (3 mm × 0.2 mm + caps) + the circle OUTLINE (not a disk).
        double ideal = 3e-3 * 0.2e-3 + Math.PI * 0.1e-3 * 0.1e-3          // path + caps
                     + Math.PI * 2e-3 * 0.1e-3;                            // ring ≈ 2πr·w
        Assert.Equal(ideal, net.Area, ideal * 5e-2);
        // Geometry landed at the flash location, and the hollow circle kept its opening.
        Assert.Contains(net.Islands, i => i.Shape.Holes.Count > 0);
        Assert.All(net.Islands, i =>
        {
            var (minX, _, maxX, _) = i.Bounds();
            Assert.InRange(minX, 8.8e-3, 14e-3);
            Assert.InRange(maxX, 8.8e-3, 14e-3);
        });
    }

    [Fact]
    public void XformMirror_FlipsAnAsymmetricFlash()
    {
        // An asymmetric RectCorner pad (extends only to +x locally) flashed with
        // mirror="true": the copper must land on the −x side of the location. The old
        // behavior (warn + ignore) would put it at +x — sign-decisive.
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Features>
                  <Location x="10" y="5"/>
                  <Xform rotation="0" mirror="true"/>
                  <RectCorner lowerLeftX="1" lowerLeftY="-0.5" upperRightX="3" upperRightY="0.5"/>
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));

        Assert.Empty(board.Warnings);
        var island = board.Nets.Single(n => n.Name == "X").Islands.Single();
        var (minX, _, maxX, _) = island.Bounds();
        Assert.Equal(7e-3, minX, 1e-9);                          // 10 − 3
        Assert.Equal(9e-3, maxX, 1e-9);                          // 10 − 1
    }

    [Fact]
    public void Marking_OnAConductorLayer_DepositsItsGeometry()
    {
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Marking markingUsage="NONE">
                  <Location x="0" y="0"/>
                  <Polyline>
                    <PolyBegin x="2" y="5"/>
                    <PolyStepSegment x="8" y="5"/>
                    <LineDesc lineEnd="ROUND" lineWidth="0.3"/>
                  </Polyline>
                </Marking>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));

        Assert.Empty(board.Warnings);
        var net = board.Nets.Single(n => n.Name == "X");
        double ideal = 6e-3 * 0.3e-3 + Math.PI * 0.15e-3 * 0.15e-3;
        Assert.Equal(ideal, net.Area, ideal * 2e-2);
    }

    [Fact]
    public void LocalFiducial_BecomesCopper()
    {
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <LocalFiducial>
                  <Location x="9.167" y="3.087"/>
                  <Circle diameter="1"/>
                </LocalFiducial>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));

        Assert.Empty(board.Warnings);
        var net = board.Nets.Single(n => n.Name == "X");
        Assert.Single(net.Islands);
        Assert.Contains(board.Pads, p =>
            Math.Abs(p.Center.X - 9.167e-3) < 1e-9 && Math.Abs(p.Center.Y - 3.087e-3) < 1e-9);
    }

    // ---------------- via-bridge fidelity + backdrills ----------------

    private const string SixLayerDecls = """
        <Layer name="L1" layerFunction="SIGNAL" side="TOP" polarity="POSITIVE" />
        <Layer name="L2" layerFunction="PLANE" side="INTERNAL" polarity="POSITIVE" />
        <Layer name="L3" layerFunction="SIGNAL" side="INTERNAL" polarity="POSITIVE" />
        <Layer name="L4" layerFunction="PLANE" side="INTERNAL" polarity="POSITIVE" />
        <Layer name="L5" layerFunction="SIGNAL" side="INTERNAL" polarity="POSITIVE" />
        <Layer name="L6" layerFunction="SIGNAL" side="BOTTOM" polarity="POSITIVE" />
        <Layer name="DRILL_ALL" layerFunction="DRILL" side="ALL" polarity="POSITIVE">
          <Span fromLayer="L1" toLayer="L6"/>
        </Layer>
        <Layer name="BD_TOP" layerFunction="DRILL" side="TOP" polarity="POSITIVE">
          <Span fromLayer="L1" toLayer="L2"/>
        </Layer>
        """;

    /// <summary>Same-net pads at (5, 5) on L1/L3/L6 + a through via there (no padstack
    /// declaration — the Cadence pattern), plus an optional backdrill block.</summary>
    private static string SixLayerViaStep(string extra = "") => RectProfile + """
        <LayerFeature layerRef="L1"><Set net="V"><Features>
          <Location x="5" y="5"/><Circle diameter="0.6"/>
        </Features></Set></LayerFeature>
        <LayerFeature layerRef="L3"><Set net="V"><Features>
          <Location x="5" y="5"/><Circle diameter="0.6"/>
        </Features></Set></LayerFeature>
        <LayerFeature layerRef="L6"><Set net="V"><Features>
          <Location x="5" y="5"/><Circle diameter="0.6"/>
        </Features></Set></LayerFeature>
        <LayerFeature layerRef="DRILL_ALL"><Set net="V">
          <Hole name="H1" diameter="0.3" platingStatus="VIA" x="5" y="5"/>
        </Set></LayerFeature>
        """ + extra;

    private const string BackdrillSpec = """
        <Spec name="BD_SPEC">
          <Backdrill type="START_LAYER"><Property text="L1"/></Backdrill>
          <Backdrill type="MUST_NOT_CUT_LAYER"><Property text="L3"/></Backdrill>
          <Backdrill type="MAX_STUB_LENGTH"><Property value="0.100" unit="MM"/></Backdrill>
        </Spec>
        """;

    [Fact]
    public void ViaWithoutPadstack_BridgesTheLayersItsCopperTouches()
    {
        // The Cadence pattern: no PadStack/PadStackDef anywhere — the old span-endpoint
        // fallback bridged only {L1, L6}, silently missing the inner L3 connection.
        var board = Read(Document(SixLayerDecls, "", SixLayerViaStep()));

        Assert.Empty(board.Warnings);
        var net = board.Nets.Single(n => n.Name == "V");
        var bridge = Assert.Single(net.StitchingVias);
        Assert.Equal(new[] { 1, 3, 6 }, bridge.Layers);
    }

    [Fact]
    public void Backdrill_SeversTheDrilledLayers_AndProtectsMustNotCut()
    {
        // The backdrill spans L1–L2 (MUST_NOT_CUT L3 sits safely below): the via keeps
        // exactly its remaining copper connections {L3, L6}; the backdrill hole itself
        // never becomes a via.
        var extra = """
            <LayerFeature layerRef="BD_TOP"><Set>
              <SpecRef id="BD_SPEC"/>
              <Hole name="BD1" diameter="0.5" platingStatus="NONPLATED" x="5" y="5"/>
            </Set></LayerFeature>
            """;
        var board = Read(Document(SixLayerDecls + BackdrillSpec, "", SixLayerViaStep(extra)));

        Assert.Empty(board.Warnings);
        Assert.Single(board.Vias);                               // H1 only — BD1 is not a hole
        var net = board.Nets.Single(n => n.Name == "V");
        var bridge = Assert.Single(net.StitchingVias);
        Assert.Equal(new[] { 3, 6 }, bridge.Layers);
        Assert.Contains(board.Notes, n => n.Contains("severed"));
    }

    [Fact]
    public void Backdrill_ElsewhereOnTheBoard_TouchesNothing()
    {
        var extra = """
            <LayerFeature layerRef="BD_TOP"><Set>
              <SpecRef id="BD_SPEC"/>
              <Hole name="BD1" diameter="0.5" platingStatus="NONPLATED" x="12" y="5"/>
            </Set></LayerFeature>
            """;
        var board = Read(Document(SixLayerDecls + BackdrillSpec, "", SixLayerViaStep(extra)));

        var bridge = Assert.Single(board.Nets.Single(n => n.Name == "V").StitchingVias);
        Assert.Equal(new[] { 1, 3, 6 }, bridge.Layers);
        Assert.DoesNotContain(board.Notes, n => n.Contains("severed"));
    }

    [Fact]
    public void Backdrill_ProtectedLayerInsideItsOwnSpan_WarnsAndKeepsTheLayer()
    {
        // A self-contradictory declaration: the drill span covers L1–L3 but the spec
        // protects L3 — honor the protection (L3 stays connected) and say so.
        const string contradictory = """
            <Spec name="BD_SPEC">
              <Backdrill type="START_LAYER"><Property text="L1"/></Backdrill>
              <Backdrill type="MUST_NOT_CUT_LAYER"><Property text="L3"/></Backdrill>
            </Spec>
            <Layer name="BD_DEEP" layerFunction="DRILL" side="TOP" polarity="POSITIVE">
              <Span fromLayer="L1" toLayer="L3"/>
            </Layer>
            """;
        var extra = """
            <LayerFeature layerRef="BD_DEEP"><Set>
              <SpecRef id="BD_SPEC"/>
              <Hole name="BD1" diameter="0.5" platingStatus="NONPLATED" x="5" y="5"/>
            </Set></LayerFeature>
            """;
        var board = Read(Document(SixLayerDecls + contradictory, "", SixLayerViaStep(extra)));

        Assert.Contains(board.Warnings, w => w.Contains("protects layer 'L3' inside its own drill span"));
        var bridge = Assert.Single(board.Nets.Single(n => n.Name == "V").StitchingVias);
        Assert.Equal(new[] { 3, 6 }, bridge.Layers);             // L1/L2 severed, L3 protected
    }

    // ---------------- SlotCavity: cutouts + plated-slot bridging ----------------

    /// <summary>An oblong slot 2×0.5 mm at (5, 5), repeated on both copper layers (the
    /// Altium pattern — one named slot per LayerFeature it passes through), inside
    /// same-net copper rectangles on both layers.</summary>
    private static string SlotStep(string plating) => RectProfile + $"""
        <LayerFeature layerRef="Top Layer"><Set net="V"><Features>
          <Location x="5" y="5"/>
          <RectCenter width="4" height="2"/>
        </Features>
        <SlotCavity name="S1" platingStatus="{plating}">
          <Outline><Polygon>
            <PolyBegin x="5.75" y="4.75"/>
            <PolyStepCurve x="5.75" y="5.25" centerX="5.75" centerY="5" clockwise="false"/>
            <PolyStepSegment x="4.25" y="5.25"/>
            <PolyStepCurve x="4.25" y="4.75" centerX="4.25" centerY="5" clockwise="false"/>
            <PolyStepSegment x="5.75" y="4.75"/>
          </Polygon></Outline>
        </SlotCavity>
        </Set></LayerFeature>
        <LayerFeature layerRef="Bottom Layer"><Set net="V"><Features>
          <Location x="5" y="5"/>
          <RectCenter width="4" height="2"/>
        </Features>
        <SlotCavity name="S1" platingStatus="{plating}">
          <Outline><Polygon>
            <PolyBegin x="5.75" y="4.75"/>
            <PolyStepCurve x="5.75" y="5.25" centerX="5.75" centerY="5" clockwise="false"/>
            <PolyStepSegment x="4.25" y="5.25"/>
            <PolyStepCurve x="4.25" y="4.75" centerX="4.25" centerY="5" clockwise="false"/>
            <PolyStepSegment x="5.75" y="4.75"/>
          </Polygon></Outline>
        </SlotCavity>
        </Set></LayerFeature>
        """;

    [Fact]
    public void PlatedSlot_BridgesItsCopper_AndCutsTheOutline()
    {
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, SlotStep("PLATED")));

        Assert.Empty(board.Warnings);
        // The two per-layer occurrences aggregate to ONE slot: one outline cutout.
        var hole = Assert.Single(board.Outline[0].Holes);
        Assert.Contains(hole, p => Math.Abs(p.X - 5.75e-3) < 1e-6);
        // The plating bridges the net's copper: barrels along the 2 mm axis, each
        // joining both layers, welded into ONE net V.
        var net = board.Nets.Single(n => n.Name == "V");
        Assert.True(net.StitchingVias.Count >= 2, $"{net.StitchingVias.Count} barrels");
        Assert.All(net.StitchingVias, b => Assert.Equal(new[] { 1, 2 }, b.Layers));
        Assert.Contains(board.Notes, n => n.Contains("slot"));
    }

    [Fact]
    public void NonplatedSlot_CutsTheOutline_AndBridgesNothing()
    {
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, SlotStep("NONPLATED")));

        Assert.Empty(board.Warnings);
        Assert.Single(board.Outline[0].Holes);
        Assert.Empty(board.Vias);
        var net = board.Nets.Single(n => n.Name == "V");
        Assert.Empty(net.StitchingVias);
    }

    [Fact]
    public void PlatedSlot_TouchingTwoNets_WarnsAndDoesNotBridge()
    {
        // Copper of DIFFERENT nets on the two layers: a plated slot would short them —
        // warn and refuse, never silently weld two named nets.
        var step = SlotStep("PLATED").Replace(
            "<LayerFeature layerRef=\"Bottom Layer\"><Set net=\"V\">",
            "<LayerFeature layerRef=\"Bottom Layer\"><Set net=\"W\">");
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));

        Assert.Contains(board.Warnings, w => w.Contains("plated slot 'S1'") && w.Contains("2 different nets"));
        Assert.Empty(board.Nets.Single(n => n.Name == "V").StitchingVias);
        Assert.Empty(board.Nets.Single(n => n.Name == "W").StitchingVias);
    }

    [Fact]
    public void RepeatedWarnings_CollapseToOneEntryWithACount()
    {
        // The log-flood guard: one problem repeated N times (differing only in its line
        // position) seals to a single entry with an (×N) suffix.
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="X">
                <Features>
                  <BogusFeature someAttr="1" />
                  <BogusFeature someAttr="2" />
                  <BogusFeature someAttr="3" />
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));

        var bogus = board.Warnings.Where(w => w.Contains("<BogusFeature>")).ToList();
        var single = Assert.Single(bogus);
        Assert.EndsWith("(×3)", single);
    }

    [Fact]
    public void NonConductorLayerFeatures_AreIgnored_AndSiblingsSurvive()
    {
        // A paste LayerFeature between two copper ones — the streaming skip must not
        // swallow the following sibling (the classic XmlReader.Skip off-by-one).
        var step = RectProfile + """
            <LayerFeature layerRef="Paste">
              <Set net="ignored"><Features>
                <Line startX="0" startY="0" endX="5" endY="0">
                  <LineDesc lineEnd="ROUND" lineWidth="0.3" lineProperty="SOLID" />
                </Line>
              </Features></Set>
            </LayerFeature>
            <LayerFeature layerRef="Top Layer">
              <Set net="A"><Features>
                <Line startX="2" startY="5" endX="10" endY="5">
                  <LineDesc lineEnd="ROUND" lineWidth="0.4" lineProperty="SOLID" />
                </Line>
              </Features></Set>
            </LayerFeature>
            """;
        var layers = """
            <Layer name="Paste" layerFunction="PASTEMASK" side="TOP" polarity="POSITIVE" />
            """ + TwoLayerDecls;
        var board = Read(Document(layers, TwoLayerStackup, step));
        Assert.Contains(board.Nets, n => n.Name == "A");
        Assert.DoesNotContain(board.Nets, n => n.Name == "ignored");
    }

    // ---------------- trace centerlines (PEEC impedance input) ----------------

    [Fact]
    public void TraceCenterlines_AreRetained_AndFeedTheImpedancePipeline()
    {
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, RectProfile + TraceStep));

        // The two RS+ Line draws survive the polygon union as exact centerlines.
        Assert.Equal(2, board.TraceCenterlines.Count);
        Assert.All(board.TraceCenterlines, c =>
        {
            Assert.Equal(1, c.LayerOrder);
            Assert.Equal(0.4e-3, c.Width, 1e-12);
        });
        Assert.Contains(board.TraceCenterlines, c => Math.Abs(c.Length - 8e-3) < 1e-9);
        Assert.Contains(board.TraceCenterlines, c => Math.Abs(c.Length - 6e-3) < 1e-9);

        // Full pipeline, exactly what the app runs after "Mesh selected net":
        // extractor → chain builder → lumped impedance estimate.
        var net = board.Nets.Single(n => n.Name == "RS+");
        var chain = OpenSim.Pcb.Inductance.TraceChainBuilder.Build(
            OpenSim.Pcb.Inductance.NetTraceExtractor.ForNet(board, net));
        Assert.NotNull(chain.Chain);
        Assert.Equal(2, chain.Chain!.Count);

        var report = OpenSim.Pcb.Inductance.NetImpedanceEstimator.Estimate(
            0.005, chain.Chain!, 35e-6, 1e3, 1e8, 3);
        Assert.True(report.InductanceHenries > 1e-9,
            "A 14 mm trace should compose to several nanohenries.");
    }

    [Fact]
    public void ArcTrace_ChordsAccumulateAboveTheStubTolerance_AndChainStillBuilds()
    {
        // A 1.0 mm-wide trace: the chain builder drops segments shorter than width/2
        // = 0.5 mm, and a 4 mm-radius arc tessellates (5 µm sagitta) to ~0.39 mm chords —
        // all sub-tolerance. The builder must accumulate chords, not emit droppable stubs.
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="ARC"><Features>
                <Line startX="2" startY="5" endX="10" endY="5">
                  <LineDesc lineEnd="ROUND" lineWidth="1.0" lineProperty="SOLID" />
                </Line>
                <Arc startX="10" startY="5" endX="14" endY="9" centerX="10" centerY="9" clockwise="false">
                  <LineDesc lineEnd="ROUND" lineWidth="1.0" lineProperty="SOLID" />
                </Arc>
              </Features></Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));

        Assert.True(board.TraceCenterlines.Count >= 3,
            "Expected the line plus several accumulated arc segments.");
        Assert.All(board.TraceCenterlines,
            c => Assert.True(c.Length > 0.5e-3, $"Sub-tolerance stub retained: {c.Length * 1e3:g3} mm."));

        var net = board.Nets.Single(n => n.Name == "ARC");
        var chain = OpenSim.Pcb.Inductance.TraceChainBuilder.Build(
            OpenSim.Pcb.Inductance.NetTraceExtractor.ForNet(board, net));
        Assert.NotNull(chain.Chain);

        // Chord sum ≈ line (8 mm) + quarter-arc (2π·4/4 ≈ 6.283 mm); chords under-measure
        // the arc only at the ~0.1% level for this sagitta.
        double total = chain.Chain!.Sum(c => c.Length);
        Assert.Equal(8e-3 + Math.PI * 2e-3, total, (8e-3 + Math.PI * 2e-3) * 0.02);
    }

    // ---------------- padstacks & via bridges ----------------

    private const string ViaStep = RectProfile + """
        <LayerFeature layerRef="Top Layer">
          <Set net="RS+"><Features>
            <Line startX="2" startY="5" endX="10" endY="5">
              <LineDesc lineEnd="ROUND" lineWidth="0.4" lineProperty="SOLID" />
            </Line>
          </Features></Set>
        </LayerFeature>
        <LayerFeature layerRef="Bottom Layer">
          <Set net="RS+"><Features>
            <Line startX="10" startY="5" endX="18" endY="5">
              <LineDesc lineEnd="ROUND" lineWidth="0.4" lineProperty="SOLID" />
            </Line>
          </Features></Set>
        </LayerFeature>
        <PadStack net="RS+">
          <LayerHole name="Via_1" diameter="0.45" platingStatus="VIA" x="10" y="5">
            <Span fromLayer="Top Layer" toLayer="Bottom Layer" />
          </LayerHole>
          <LayerPad layerRef="Top Layer">
            <Location x="10" y="5" />
            <Circle diameter="0.75" />
          </LayerPad>
          <LayerPad layerRef="Bottom Layer">
            <Location x="10" y="5" />
            <Circle diameter="0.75" />
          </LayerPad>
        </PadStack>
        """;

    [Fact]
    public void PlatedViaWithPads_BridgesBothLayers()
    {
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, ViaStep));
        var net = board.Nets.Single(n => n.Name == "RS+");

        Assert.Equal(new[] { 1, 2 }, net.Layers);
        var bridge = Assert.Single(net.StitchingVias);
        Assert.Equal(new[] { 1, 2 }, bridge.Layers);
        Assert.Equal(0.45e-3, bridge.Via.Diameter, 1e-9);
        Assert.True(bridge.Via.Plated);

        // Both landing pads are electrode candidates.
        Assert.Equal(2, board.Pads.Count(p => (p.Center - new Point2(10e-3, 5e-3)).Length < 1e-9));
    }

    [Fact]
    public void UnplatedHole_ProducesNoBridge()
    {
        var step = ViaStep.Replace("platingStatus=\"VIA\"", "platingStatus=\"NONPLATED\"");
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));
        var net = board.Nets.Single(n => n.Name == "RS+");
        Assert.Empty(net.StitchingVias);
        Assert.Contains(board.Vias, v => !v.Plated);
    }

    // ---------------- KiCad dialect: PadStackDef / Pad instances / DRILL holes ----------------

    /// <summary>KiCad-style layer declarations: the drill layer declares its span as a child.</summary>
    private const string KiCadDecls = """
        <Layer name="F.Cu" layerFunction="CONDUCTOR" side="TOP" polarity="POSITIVE" />
        <Layer name="DIELECTRIC_1" layerFunction="DIELCORE" side="INTERNAL" polarity="POSITIVE" />
        <Layer name="B.Cu" layerFunction="CONDUCTOR" side="BOTTOM" polarity="POSITIVE" />
        <Layer name="F.Cu_B.Cu" layerFunction="DRILL" side="ALL" polarity="POSITIVE">
          <Span fromLayer="F.Cu" toLayer="B.Cu" />
        </Layer>
        """;

    private const string KiCadStackup = """
        <StackupLayer layerOrGroupRef="F.Cu" thickness="0.035" sequence="1" />
        <StackupLayer layerOrGroupRef="DIELECTRIC_1" thickness="1.51" sequence="2" />
        <StackupLayer layerOrGroupRef="B.Cu" thickness="0.035" sequence="3" />
        """;

    private const string KiCadDictionary = """
        <EntryStandard id="CIRCLE_8"><Circle diameter="0.6" /></EntryStandard>
        <EntryStandard id="RECT_2"><RectCenter width="1.27" height="2.2" /></EntryStandard>
        """;

    /// <summary>
    /// The KiCad export shape: PadStackDef under Step, Pad instances (self-contained
    /// Location + StandardPrimitiveRef) inside copper Sets, and Hole elements inside a
    /// DRILL LayerFeature's per-net Sets.
    /// </summary>
    private const string KiCadStep = RectProfile + """
        <PadStackDef name="PADSTACK_8">
          <PadstackHoleDef name="PH300000" diameter="0.30" platingStatus="VIA" x="0.0" y="0.0" />
          <PadstackPadDef layerRef="F.Cu" padUse="REGULAR">
            <Location x="0.0" y="0.0" />
            <StandardPrimitiveRef id="CIRCLE_8" />
          </PadstackPadDef>
          <PadstackPadDef layerRef="B.Cu" padUse="REGULAR">
            <Location x="0.0" y="0.0" />
            <StandardPrimitiveRef id="CIRCLE_8" />
          </PadstackPadDef>
        </PadStackDef>
        <LayerFeature layerRef="F.Cu">
          <Set net="N1" padUsage="VIA">
            <Features>
              <Line startX="2" startY="5" endX="10" endY="5">
                <LineDesc lineEnd="ROUND" lineWidth="0.4" />
              </Line>
            </Features>
            <Pad padstackDefRef="PADSTACK_8">
              <Location x="10" y="5" />
              <StandardPrimitiveRef id="CIRCLE_8" />
            </Pad>
          </Set>
          <Set net="N1">
            <Pad padstackDefRef="PADSTACK_2">
              <Xform rotation="90.0" />
              <Location x="2" y="5" />
              <StandardPrimitiveRef id="RECT_2" />
              <PinRef componentRef="J1" pin="1" />
            </Pad>
          </Set>
        </LayerFeature>
        <LayerFeature layerRef="B.Cu">
          <Set net="N1" padUsage="VIA">
            <Features>
              <Line startX="10" startY="5" endX="18" endY="5">
                <LineDesc lineEnd="ROUND" lineWidth="0.4" />
              </Line>
            </Features>
            <Pad padstackDefRef="PADSTACK_8">
              <Location x="10" y="5" />
              <StandardPrimitiveRef id="CIRCLE_8" />
            </Pad>
          </Set>
        </LayerFeature>
        <LayerFeature layerRef="F.Cu_B.Cu">
          <Set geometry="PADSTACK_8" net="N1">
            <Hole name="H1" diameter="0.30" platingStatus="VIA" plusTol="0.0" minusTol="0.0" x="10" y="5" />
          </Set>
        </LayerFeature>
        """;

    [Fact]
    public void KiCadDialect_PadInstancesAndDrillHole_BridgeBothLayers()
    {
        var board = Read(Document(KiCadDecls, KiCadStackup, KiCadStep, dictionary: KiCadDictionary));
        var net = board.Nets.Single(n => n.Name == "N1");

        // The via hole from the DRILL LayerFeature bridges F.Cu → B.Cu.
        Assert.Equal(new[] { 1, 2 }, net.Layers);
        var bridge = Assert.Single(net.StitchingVias);
        Assert.Equal(new[] { 1, 2 }, bridge.Layers);
        Assert.Equal(0.30e-3, bridge.Via.Diameter, 1e-9);
        Assert.True(bridge.Via.Plated);

        // Via landing pads on both layers + the rotated component pad = 3 electrode pads.
        Assert.Equal(3, board.Pads.Count);
        Assert.Equal(2, board.Pads.Count(p => (p.Center - new Point2(10e-3, 5e-3)).Length < 1e-9));

        // The Xform on the component pad rotates the 1.27×2.2 rectangle to 2.2×1.27.
        var component = board.Pads.Single(p => (p.Center - new Point2(2e-3, 5e-3)).Length < 1e-9);
        double minX = component.Shape.Outer.Min(p => p.X), maxX = component.Shape.Outer.Max(p => p.X);
        double minY = component.Shape.Outer.Min(p => p.Y), maxY = component.Shape.Outer.Max(p => p.Y);
        Assert.Equal(2.2e-3, maxX - minX, 1e-6);
        Assert.Equal(1.27e-3, maxY - minY, 1e-6);

        // No "unsupported feature <Pad>" noise.
        Assert.DoesNotContain(board.Warnings, w => w.Contains("<Pad>"));
    }

    [Fact]
    public void KiCadDialect_PinRefAndComponent_NamePads()
    {
        // The component pad's PinRef + the Component element give it a refdes.pin and a
        // part name (part preferred; packageRef is the fallback when part is absent).
        string step = KiCadStep + """
            <Component refDes="J1" packageRef="PKG_HDR" part="PART_HDR_2X5" layerRef="F.Cu" mountType="SMT">
              <Location x="2.0" y="5.0" />
            </Component>
            """;
        var board = Read(Document(KiCadDecls, KiCadStackup, step, dictionary: KiCadDictionary));

        var component = board.Pads.Single(p => (p.Center - new Point2(2e-3, 5e-3)).Length < 1e-9);
        Assert.Equal("J1", component.ComponentRef);
        Assert.Equal("1", component.Pin);
        Assert.Equal("PART_HDR_2X5", component.PartName);

        // Via landing pads carry no PinRef — identity stays null, never fabricated.
        Assert.All(board.Pads.Where(p => (p.Center - new Point2(10e-3, 5e-3)).Length < 1e-9),
            pad =>
            {
                Assert.Null(pad.ComponentRef);
                Assert.Null(pad.PartName);
            });
    }

    [Fact]
    public void KiCadDialect_ComponentWithoutPart_FallsBackToPackage()
    {
        string step = KiCadStep + """
            <Component refDes="J1" packageRef="PKG_HDR" layerRef="F.Cu" mountType="SMT">
              <Location x="2.0" y="5.0" />
            </Component>
            """;
        var board = Read(Document(KiCadDecls, KiCadStackup, step, dictionary: KiCadDictionary));

        var component = board.Pads.Single(p => (p.Center - new Point2(2e-3, 5e-3)).Length < 1e-9);
        Assert.Equal("PKG_HDR", component.PartName);
    }

    [Fact]
    public void KiCadDialect_UnknownPadstackRef_FallsBackToDrillSpan()
    {
        // Drop the PadStackDef: the hole's pad layers fall back to the drill layer's
        // declared span, so the plated via still bridges F.Cu → B.Cu.
        int defStart = KiCadStep.IndexOf("<PadStackDef", StringComparison.Ordinal);
        int defEnd = KiCadStep.IndexOf("</PadStackDef>", StringComparison.Ordinal) + "</PadStackDef>".Length;
        var step = KiCadStep[..defStart] + KiCadStep[defEnd..];

        var board = Read(Document(KiCadDecls, KiCadStackup, step, dictionary: KiCadDictionary));
        var net = board.Nets.Single(n => n.Name == "N1");
        var bridge = Assert.Single(net.StitchingVias);
        Assert.Equal(new[] { 1, 2 }, bridge.Layers);
    }

    [Fact]
    public void KiCadDialect_NonPlatedHole_ProducesNoBridge()
    {
        var step = KiCadStep.Replace(
            "<Hole name=\"H1\" diameter=\"0.30\" platingStatus=\"VIA\"",
            "<Hole name=\"H1\" diameter=\"0.30\" platingStatus=\"NONPLATED\"");
        var board = Read(Document(KiCadDecls, KiCadStackup, step, dictionary: KiCadDictionary));
        var net = board.Nets.Single(n => n.Name == "N1");
        Assert.Empty(net.StitchingVias);
        Assert.Contains(board.Vias, v => !v.Plated);
    }

    // ---------------- primitive dictionary ----------------

    [Fact]
    public void DictionaryPrimitives_FlashWithRotation()
    {
        var dictionary = """
            <EntryStandard id="R1"><RectCenter width="2" height="1" /></EntryStandard>
            """;
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="P">
                <Features>
                  <Xform rotation="90" />
                  <Location x="10" y="5" />
                  <StandardPrimitiveRef id="R1" />
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step, dictionary: dictionary));
        var net = board.Nets.Single(n => n.Name == "P");
        var island = Assert.Single(net.Islands);

        // Rotated 90°: the 2×1 mm rectangle's bounds swap to 1×2 mm.
        var b = island.Bounds();
        Assert.Equal(1e-3, b.MaxX - b.MinX, 1e-6);
        Assert.Equal(2e-3, b.MaxY - b.MinY, 1e-6);
    }

    [Fact]
    public void UnknownPrimitiveRef_WarnsAndSkips()
    {
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="P">
                <Features>
                  <Location x="10" y="5" />
                  <StandardPrimitiveRef id="MISSING" />
                </Features>
              </Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));
        Assert.DoesNotContain(board.Nets, n => n.Name == "P");
        Assert.Contains(board.Warnings, w => w.Contains("MISSING"));
    }

    // ---------------- error handling ----------------

    [Fact]
    public void MalformedXml_ThrowsInvalidData()
    {
        var xml = Document(TwoLayerDecls, TwoLayerStackup, RectProfile);
        xml = xml[..(xml.Length / 2)];                           // truncate mid-document
        var ex = Assert.Throws<InvalidDataException>(() => Parse(xml));
        Assert.Contains("well-formed", ex.Message);
    }

    [Fact]
    public void BadNumber_WarnsAndSkipsFeatureOnly()
    {
        var step = RectProfile + """
            <LayerFeature layerRef="Top Layer">
              <Set net="A"><Features>
                <Line startX="abc" startY="5" endX="10" endY="5">
                  <LineDesc lineEnd="ROUND" lineWidth="0.4" lineProperty="SOLID" />
                </Line>
                <Line startX="2" startY="5" endX="10" endY="5">
                  <LineDesc lineEnd="ROUND" lineWidth="0.4" lineProperty="SOLID" />
                </Line>
              </Features></Set>
            </LayerFeature>
            """;
        var board = Read(Document(TwoLayerDecls, TwoLayerStackup, step));
        var net = board.Nets.Single(n => n.Name == "A");
        Assert.Single(net.Islands);                              // the good trace survived
        Assert.Contains(board.Warnings, w => w.Contains("malformed"));
    }

    [Fact]
    public void UnsupportedUnits_Throw()
    {
        Assert.Throws<InvalidDataException>(() =>
            Parse(Document(TwoLayerDecls, TwoLayerStackup, RectProfile, units: "FURLONG")));
    }
}

/// <summary>End-to-end parse of the real 4-layer example export at the repo root.</summary>
public class Ipc2581IntegrationTests
{
    private static string? FindExampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Example_IPC-2581.cvg");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    internal static string? FindKiCadExampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Breakout_Board.xml");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    internal static string? FindCadenceExampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Large_Board_Example.xml");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// The transform oracle that pins the Xform mirror convention empirically:
    /// instantiating every Cadence component's LandPattern pads under the component's
    /// placement (ROTATE first, then mirror x → −x) must land on the placed
    /// conductor-layer pad flashes of the same componentRef + pin. Measured across the
    /// candidate conventions: rotate-then-mirrorX 1689/1689, mirrorX-then-rotate
    /// 523/1417 (mirrored side), rotate-then-mirrorY 56/1417, no-mirror 350/1417 —
    /// the match rate collapses for every wrong convention, so the gate is
    /// sign-decisive where any single synthetic fixture could be coincidentally right.
    /// </summary>
    [Fact]
    public void CadenceBoard_ComponentInstantiation_LandsOnPlacedPads()
    {
        string? path = FindCadenceExampleFile();
        if (path is null) return;                                // example not present
        var design = new Ipc2581Parser().Parse(path);

        // Placed conductor pads by (componentRef, pin) → centers.
        var placed = new Dictionary<(string, string), List<OpenSim.Core.Geometry2D.Point2>>();
        foreach (var net in design.Nets.Values)
            foreach (var pad in net.Pads)
                if (pad.ComponentRef is not null && pad.Pin is not null)
                {
                    var key = (pad.ComponentRef, pad.Pin);
                    if (!placed.TryGetValue(key, out var list)) placed[key] = list = new();
                    list.Add(pad.Center);
                }
        // Measured: 1,689 distinct (refdes, pin) keys on copper (7.5k flashes — a
        // through-pin lands one flash per bridged layer).
        Assert.True(placed.Count > 1500, $"only {placed.Count} component pins on copper");

        int total = 0, matched = 0, mirrored = 0;
        foreach (var component in design.Components)
        {
            if (component.Location is null || component.PackageRef is null) continue;
            if (!design.Packages.TryGetValue(component.PackageRef, out var package)) continue;
            if (component.Mirror) mirrored++;
            foreach (var pad in package.Pads)
            {
                if (pad.Pin is null) continue;
                if (!placed.TryGetValue((component.RefDes, pad.Pin), out var centers)) continue;
                total++;
                var expected = component.Transform.Apply(pad.Location);
                if (centers.Any(c => (c - expected).Length < 10e-6)) matched++;
            }
        }

        Assert.True(total > 1500, $"only {total} instantiable pads");
        Assert.True(mirrored > 50, $"only {mirrored} mirrored components — the gate needs both sides");
        Assert.True(matched >= total * 0.99,
            $"only {matched}/{total} instantiated pads land on placed copper — mirror convention wrong?");
    }

    /// <summary>
    /// The KiCad-dialect gate on a real KiCad 2-layer export: pads come as Pad
    /// instances in copper Sets and drills as Hole elements in a DRILL LayerFeature —
    /// both invisible to the original (Cadence-style) reader. Counts observed when the
    /// dialect support landed: 484 copper pad flashes (337 F.Cu + 147 B.Cu; mask/paste
    /// flashes correctly skipped), 149 drill holes, nets bridging F.Cu ↔ B.Cu.
    /// </summary>
    [Fact]
    public void KiCadBoard_PadsAndViasImport()
    {
        string? path = FindKiCadExampleFile();
        if (path is null) return;                                // example not present in this checkout

        var board = new Ipc2581Reader().Read(path);

        Assert.Equal(2, board.Layers.Count);                     // F.Cu, B.Cu
        Assert.True(board.Pads.Count > 450, $"only {board.Pads.Count} pads found");
        Assert.True(board.Vias.Count(v => v.Plated) > 100,
            $"only {board.Vias.Count(v => v.Plated)} plated vias found");
        Assert.Contains(board.Nets, n => n.StitchingVias.Count > 0 && n.Layers.Count == 2);
        Assert.DoesNotContain(board.Warnings, w => w.Contains("<Pad>"));

        // Stackup straight from the file: 35 µm copper, 1.51 mm core.
        Assert.NotNull(board.Stackup);
        Assert.All(board.Stackup!.CopperLayerThicknesses, t => Assert.Equal(35e-6, t, 1e-9));
        Assert.Equal(1.51e-3, board.Stackup.DielectricGapThicknesses.Single(), 1e-9);

        // Pad identity from the file's PinRef + Component data. Measured against the
        // raw XML: exactly the 190 SMD component pads on F.Cu carry a PinRef — the
        // remaining copper flashes are via landing pads, which have no component pin
        // (the file's other PinRefs sit on mask/paste flashes of layers we skip).
        var named = board.Pads.Where(p => p.ComponentRef is not null).ToList();
        Assert.True(named.Count > 150, $"only {named.Count} pads carry a PinRef identity");
        Assert.True(named.Count < board.Pads.Count, "via landing pads must stay anonymous");
        Assert.All(named, p =>
        {
            Assert.False(string.IsNullOrEmpty(p.Pin));
            Assert.False(string.IsNullOrEmpty(p.PartName));
        });
    }

    [Fact]
    public void KiCadBoard_ViaBridgedNet_MeshesAsOneBody()
    {
        string? path = FindKiCadExampleFile();
        if (path is null) return;

        var board = new Ipc2581Reader().Read(path);

        // The user workflow that surfaced the bug: pick a small named net that crosses
        // F.Cu → B.Cu through a via and mesh it — the barrel must join the layers.
        var net = board.Nets
            .Where(n => n.Name is not null && n.Layers.Count == 2 && n.StitchingVias.Count > 0)
            .OrderBy(n => n.Area)
            .First();

        var thickness = new Dictionary<int, double>();
        for (int i = 0; i < board.Stackup!.CopperLayerThicknesses.Count; i++)
            thickness[i + 1] = board.Stackup.CopperLayerThicknesses[i];
        var gaps = new Dictionary<int, double>();
        for (int i = 0; i < board.Stackup.DielectricGapThicknesses.Count; i++)
            gaps[i + 1] = board.Stackup.DielectricGapThicknesses[i];

        var result = new OpenSim.Pcb.Import.NetMesher().MeshNet(net, board.Pads,
            new OpenSim.Pcb.Import.NetMeshOptions
            {
                LayerThickness = thickness,
                DielectricGapThickness = gaps
            });

        Assert.True(result.Body.Mesh!.ElementCount > 0);
        Assert.True(result.Pads.Count >= 1,
            $"net '{net.Name}' should expose pad electrodes, got {result.Pads.Count}");
    }

    /// <summary>
    /// The vertex-cap removal gate: the KiCad board's ground pour (far beyond the old
    /// 3000-vertex cap) must mesh as a solvable body with pad electrodes, with the
    /// advisory plane/pour warning in place of the old hard rejection.
    /// </summary>
    [Fact]
    public void KiCadBoard_GroundPour_MeshesWithoutCap()
    {
        string? path = FindKiCadExampleFile();
        if (path is null) return;

        var board = new Ipc2581Reader().Read(path);
        var net = board.Nets.Where(n => n.Name is not null).OrderByDescending(n => n.Area).First();

        var thickness = new Dictionary<int, double>();
        for (int i = 0; i < board.Stackup!.CopperLayerThicknesses.Count; i++)
            thickness[i + 1] = board.Stackup.CopperLayerThicknesses[i];
        var gaps = new Dictionary<int, double>();
        for (int i = 0; i < board.Stackup.DielectricGapThicknesses.Count; i++)
            gaps[i + 1] = board.Stackup.DielectricGapThicknesses[i];

        var result = new OpenSim.Pcb.Import.NetMesher().MeshNet(net, board.Pads,
            new OpenSim.Pcb.Import.NetMeshOptions
            {
                LayerThickness = thickness,
                DielectricGapThickness = gaps
            });

        Assert.True(result.Body.Mesh!.ElementCount > 0);
        Assert.True(result.Pads.Count >= 1,
            $"pour net '{net.Name}' should expose pad electrodes, got {result.Pads.Count}");
    }

    /// <summary>
    /// The vertex-cap removal gate on the 4-layer board: the via-stitched GND pour
    /// (3 layers, ~117 barrels, ~9.9k outline vertices — over 3× the old cap) meshes
    /// as one conformal body. Measured ~10 s when the cap was removed.
    /// </summary>
    [Fact]
    public void ExampleBoard_GroundPour_MeshesWithoutCap()
    {
        string? path = FindExampleFile();
        if (path is null) return;

        var board = new Ipc2581Reader().Read(path);
        var net = board.Nets.Where(n => n.Name is not null).OrderByDescending(n => n.Area).First();

        var result = new OpenSim.Pcb.Import.NetMesher().MeshNet(net, board.Pads,
            new OpenSim.Pcb.Import.NetMeshOptions());

        Assert.True(result.Body.Mesh!.ElementCount > 0);
        Assert.True(result.Pads.Count >= 2,
            $"pour net '{net.Name}' should expose pad electrodes, got {result.Pads.Count}");
    }

    [Fact]
    public void ExampleBoard_ParsesWithRealNetsAndStackup()
    {
        string? path = FindExampleFile();
        if (path is null) return;                                // example not present in this checkout

        var board = new Ipc2581Reader().Read(path);

        Assert.NotEmpty(board.Outline);
        Assert.Equal(4, board.Layers.Count);                     // Top, Layer 1, Layer 2, Bottom
        Assert.Contains(board.Nets, n => n.Name == "RS+");
        Assert.Contains(board.Nets, n => n.Name == "+3v3");
        Assert.True(board.Nets.Count > 20, $"only {board.Nets.Count} nets found");
        Assert.True(board.Pads.Count > 500, $"only {board.Pads.Count} pads found");
        Assert.Contains(board.Nets, n => n.StitchingVias.Count > 0);

        // Stackup straight from the file: 35.56 µm outer / 35.001 µm inner copper,
        // 71.12/320.04/71.12 µm dielectric gaps.
        Assert.NotNull(board.Stackup);
        Assert.Collection(board.Stackup!.CopperLayerThicknesses,
            t => Assert.Equal(35.56e-6, t, 1e-9),
            t => Assert.Equal(35.001e-6, t, 1e-9),
            t => Assert.Equal(35.001e-6, t, 1e-9),
            t => Assert.Equal(35.56e-6, t, 1e-9));
        Assert.Collection(board.Stackup.DielectricGapThicknesses,
            g => Assert.Equal(71.12e-6, g, 1e-9),
            g => Assert.Equal(320.04e-6, g, 1e-9),
            g => Assert.Equal(71.12e-6, g, 1e-9));
    }

    [Fact]
    public void ExampleBoard_SmallNamedNet_MeshesWithElectrodes()
    {
        string? path = FindExampleFile();
        if (path is null) return;

        var board = new Ipc2581Reader().Read(path);

        // The app workflow: pick a small named signal net, mesh it, get electrodes.
        var net = board.Nets
            .Where(n => n.Name is not null && n.IsSingleLayer)
            .Where(n => board.Pads.Count(p => p.LayerOrder == n.Layers[0]
                && n.Islands.Any(i => OpenSim.Pcb.Meshing2D.PlanarMesher.ContainsPoint(
                    new[] { i.Shape }, p.Center))) >= 2)
            .OrderBy(n => n.Area)
            .First();

        var result = new OpenSim.Pcb.Import.NetMesher().MeshNet(net, board.Pads,
            new OpenSim.Pcb.Import.NetMeshOptions
            {
                LayerThickness = new Dictionary<int, double>
                    { [net.Layers[0]] = board.Stackup!.CopperLayerThicknesses[net.Layers[0] - 1] }
            });

        Assert.True(result.Body.Mesh!.ElementCount > 0);
        Assert.True(result.Pads.Count >= 2,
            $"net '{net.Name}' should expose at least source+sink electrodes, got {result.Pads.Count}");
    }

    /// <summary>
    /// The board-wide chain-composability gate: on the real 4-layer example export,
    /// pad-anchored path extraction (dead branches pruned — zero current under
    /// pad-to-pad drive) must rescue the genuinely branched signal nets that the
    /// whole-net build rightly refuses, and the union of both strategies (the app falls
    /// back from anchored to plain) covers the great majority of routed signal nets.
    /// Counts observed when the feature landed: 106 signal nets with traces, 82 plain,
    /// 90 anchored, 99 union — floors asserted so a regression in clustering, dedup,
    /// stub handling, or path extraction shows up as a falling count.
    /// </summary>
    [Fact]
    public void ExampleBoard_PadAnchoredChains_ComposeAcrossTheBoard()
    {
        string? path = FindExampleFile();
        if (path is null) return;
        var board = new Ipc2581Reader().Read(path);
        var options = new OpenSim.Pcb.Import.NetMeshOptions
        {
            CopperThickness = 35e-6,
            DefaultDielectricThickness = 1.6e-3,
            ViaPlatingThickness = 25e-6
        };
        var plane = new System.Text.RegularExpressions.Regex(
            @"GND|GROUND|POWER|PWR|VCC|VDD|PLANE|\+[0-9]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        int withTraces = 0, plainOk = 0, anchoredOk = 0, unionOk = 0;
        var results = new Dictionary<string, (OpenSim.Pcb.Inductance.TraceChain3DResult Plain,
            OpenSim.Pcb.Inductance.TraceChain3DResult Anchored)>();
        foreach (var net in board.Nets)
        {
            if (net.Name is null || plane.IsMatch(net.Name)) continue;
            var traces = OpenSim.Pcb.Inductance.NetTraceExtractor.ForNet(board, net);
            if (traces.Count == 0) continue;
            withTraces++;

            var pads = OpenSim.Pcb.Inductance.NetTraceExtractor.PadsForNet(board, net);
            (OpenSim.Pcb.Inductance.ChainTerminal, OpenSim.Pcb.Inductance.ChainTerminal)? terminals = null;
            if (pads.Count >= 2)
            {
                double best = -1; int bi = 0, bj = 1;
                for (int i = 0; i < pads.Count; i++)
                    for (int j = i + 1; j < pads.Count; j++)
                    {
                        double dx = pads[i].Center.X - pads[j].Center.X;
                        double dy = pads[i].Center.Y - pads[j].Center.Y;
                        if (dx * dx + dy * dy > best) { best = dx * dx + dy * dy; bi = i; bj = j; }
                    }
                terminals = (new OpenSim.Pcb.Inductance.ChainTerminal(pads[bi].Center, pads[bi].LayerOrder),
                             new OpenSim.Pcb.Inductance.ChainTerminal(pads[bj].Center, pads[bj].LayerOrder));
            }

            var plainR = OpenSim.Pcb.Inductance.TraceChainBuilder.Build(
                traces, net.StitchingVias, options, net.Islands);
            var anchoredR = terminals is null ? plainR : OpenSim.Pcb.Inductance.TraceChainBuilder.Build(
                traces, net.StitchingVias, options, net.Islands, null, terminals);
            if (plainR.Chain is not null) plainOk++;
            if (anchoredR.Chain is not null) anchoredOk++;
            if (plainR.Chain is not null || anchoredR.Chain is not null) unionOk++;
            results[net.Name] = (plainR, anchoredR);
        }

        Assert.True(withTraces >= 100, $"only {withTraces} signal nets with traces");
        Assert.True(plainOk >= 82, $"only {plainOk} nets compose as whole-net chains");
        Assert.True(anchoredOk >= 90, $"only {anchoredOk} nets compose pad-anchored");
        Assert.True(unionOk >= 99, $"only {unionOk} nets compose by either strategy");

        // The user-reported branch case: SWDIO is a genuine multi-pad T topology —
        // refused whole-net, composed pad-anchored with the dead branches pruned.
        Assert.Null(results["SWDIO"].Plain.Chain);
        Assert.Contains("branches", results["SWDIO"].Plain.FailureReason);
        Assert.NotNull(results["SWDIO"].Anchored.Chain);
        Assert.True(results["SWDIO"].Anchored.PrunedSegments > 0);

        // A clean two-pad chain is untouched by anchoring (no pruning).
        Assert.NotNull(results["VP_SW"].Plain.Chain);
        Assert.NotNull(results["VP_SW"].Anchored.Chain);
        Assert.Equal(0, results["VP_SW"].Anchored.PrunedSegments);

        // RS+ (kelvin sense through the shunt fill) must fail with an HONEST topology
        // reason — never the "degenerate segment" abort its swallowed jog used to cause.
        Assert.Null(results["RS+"].Anchored.Chain);
        Assert.DoesNotContain("degenerate", results["RS+"].Anchored.FailureReason);
        Assert.DoesNotContain("degenerate", results["RS+"].Plain.FailureReason);
    }
}
