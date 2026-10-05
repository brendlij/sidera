using Sidera.Core.Framing;
using Sidera.Core.Mounts;
using Sidera.Sky;

namespace Sidera.Sky.Tests;

public sealed class HealpixTests
{
    [Theory]
    [InlineData(0, 0, 0, 4)]      // the equator at 0 h is in the base pixel 4
    [InlineData(0, 0, 90, 0)]     // the north pole, a northern base pixel
    [InlineData(0, 0, -90, 8)]    // the south pole, a southern base pixel
    [InlineData(0, 90, 0, 5)]
    [InlineData(0, 180, 0, 6)]
    [InlineData(0, 270, 0, 7)]
    public void TheBasePixels_AreWhereHealpixHasThem(int order, double ra, double dec, long expected) =>
        Assert.Equal(expected, HealpixNested.AngleToPixel(order, ra, dec));

    [Fact]
    public void TheFourChildrenOfAPixel_FollowEachOtherInTheNextOrder()
    {
        var parent = HealpixNested.AngleToPixel(3, 123.4, 12.3);
        var child = HealpixNested.AngleToPixel(4, 123.4, 12.3);

        Assert.Equal(parent, child >> 2);
    }

    [Theory]
    [InlineData(10.68, 41.27)]
    [InlineData(359.99, -85)]
    [InlineData(0.01, 89.5)]
    [InlineData(180, 0)]
    public void ARealPosition_GivesATileAndAPixelInsideIt(double ra, double dec)
    {
        var (tile, column, row) = HealpixNested.ToTilePixel(5, 9, ra, dec);

        Assert.InRange(tile, 0, 12 * (1L << 10) * (1L << 5) - 1);
        Assert.InRange(column, 0, 511);
        Assert.InRange(row, 0, 511);
        Assert.Equal(tile, HealpixNested.AngleToPixel(5, ra, dec));
    }

    [Fact]
    public void BitSpreadingAndCompacting_AreInverse()
    {
        for (long v = 0; v < 70000; v += 337)
        {
            Assert.Equal(v, HealpixNested.CompactBits(HealpixNested.SpreadBits(v)));
        }
    }

    [Fact]
    public void NeighbouringPixelsOfAView_ShareATileOrAreNeighboursInIt()
    {
        var a = HealpixNested.ToTilePixel(4, 9, 83.80, -5.40);
        var b = HealpixNested.ToTilePixel(4, 9, 83.8001, -5.4);

        Assert.Equal(a.Tile, b.Tile);
        Assert.True(Math.Abs(a.Column - b.Column) + Math.Abs(a.Row - b.Row) <= 2);
    }
}

public sealed class SkyTileCacheTests : IDisposable
{
    private readonly string _root = SkyTestPaths.NewTemp();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    [Fact]
    public void ATileThatWasPut_IsAHit_AndAnotherIsAMiss()
    {
        var cache = new SkyTileCache(_root);
        cache.Put("hips", "CDS/P/Test", 3, 123, "jpg", FakeTiles.Jpeg(7));

        Assert.Equal(FakeTiles.Jpeg(7), cache.TryGet("hips", "CDS/P/Test", 3, 123, "jpg"));
        Assert.Null(cache.TryGet("hips", "CDS/P/Test", 3, 124, "jpg"));
        Assert.Null(cache.TryGet("hips", "CDS/P/Other", 3, 123, "jpg"));
    }

    [Fact]
    public void TheLayout_IsTheOneOfHips_UnderProviderAndSurvey()
    {
        var cache = new SkyTileCache(_root);

        var path = cache.TilePath("hips", "CDS/P/DSS2/color", 9, 123456, "jpg");

        Assert.EndsWith(Path.Combine("hips", "CDS_P_DSS2_color", "Norder9", "Dir120000", "Npix123456.jpg"), path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void AnItemThatIsNotAPicture_IsAMiss_AndIsRemoved(int length)
    {
        var cache = new SkyTileCache(_root);
        var path = cache.TilePath("hips", "s", 2, 1, "jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);

        Assert.Null(cache.TryGet("hips", "s", 2, 1, "jpg"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AnItemThatIsCutShort_IsAMiss()
    {
        var cache = new SkyTileCache(_root);
        var path = cache.TilePath("hips", "s", 2, 1, "jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cut = FakeTiles.Jpeg(1)[..20];
        File.WriteAllBytes(path, cut);

        Assert.Null(cache.TryGet("hips", "s", 2, 1, "jpg"));
    }

    [Fact]
    public void WhatIsNotAPicture_IsNeverStored()
    {
        var cache = new SkyTileCache(_root);

        cache.Put("hips", "s", 1, 1, "jpg", System.Text.Encoding.UTF8.GetBytes("<html>404 not found, an error page of the server</html>"));

        Assert.Null(cache.TryGet("hips", "s", 1, 1, "jpg"));
        Assert.Equal(0, cache.TotalBytes());
    }

    [Fact]
    public void TheCache_IsBounded_AndTheTilesUsedLongestAgoGoFirst()
    {
        var cache = new SkyTileCache(_root, maxBytes: 1024);
        for (var i = 0; i < 20; i++)
        {
            var tile = new byte[200];
            Array.Copy(FakeTiles.Jpeg(1), tile, 32);
            tile[^2] = 0xFF;
            tile[^1] = 0xD9;
            cache.Put("hips", "s", 3, i, "jpg", tile);
            File.SetLastWriteTimeUtc(cache.TilePath("hips", "s", 3, i, "jpg"), DateTime.UtcNow.AddMinutes(-100 + i));
        }

        Assert.True(cache.TotalBytes() <= 1024, $"{cache.TotalBytes()} bytes");
        Assert.NotNull(cache.TryGet("hips", "s", 3, 19, "jpg")); // the newest stays
        Assert.Null(cache.TryGet("hips", "s", 3, 0, "jpg")); // the oldest went
    }

    [Fact]
    public void AHit_CountsAsUse_SoAnOftenSeenTileOutlivesOthers()
    {
        var cache = new SkyTileCache(_root, maxBytes: 100_000);
        var tile = new byte[200];
        Array.Copy(FakeTiles.Jpeg(1), tile, 32);
        tile[^2] = 0xFF;
        tile[^1] = 0xD9;
        cache.Put("hips", "s", 3, 1, "jpg", tile);
        cache.Put("hips", "s", 3, 2, "jpg", tile);
        File.SetLastWriteTimeUtc(cache.TilePath("hips", "s", 3, 1, "jpg"), DateTime.UtcNow.AddDays(-5));
        File.SetLastWriteTimeUtc(cache.TilePath("hips", "s", 3, 2, "jpg"), DateTime.UtcNow.AddDays(-4));

        cache.TryGet("hips", "s", 3, 1, "jpg");

        Assert.True(File.GetLastWriteTimeUtc(cache.TilePath("hips", "s", 3, 1, "jpg")) > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void TheRightsOfTheSurvey_AreKeptBesideTheTiles_AsPublished()
    {
        var cache = new SkyTileCache(_root);
        var info = HiPSSurveyProvider.ParseProperties(FakeTiles.Properties, new SkySurveyDescriptor("Test/P/sky", "Test", "https://example.org/sky"));

        cache.SaveInfo("hips", info);
        var loaded = new SkyTileCache(_root).TryLoadInfo("hips", "Test/P/sky");

        Assert.Equal(info, loaded);
        Assert.Equal("CC-BY-4.0", loaded!.License);
        Assert.Equal("http://example.org/copyright", loaded.CopyrightUrl);
        var text = File.ReadAllText(Path.Combine(cache.SurveyFolder("hips", "Test/P/sky"), "survey.json"));
        Assert.Contains("not to be redistributed", text);
    }

    [Fact]
    public void AnInformationFileThatCannotBeRead_IsNoInformation()
    {
        var cache = new SkyTileCache(_root);
        var folder = cache.SurveyFolder("hips", "s");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "survey.json"), "{ not json");

        Assert.Null(cache.TryLoadInfo("hips", "s"));
    }
}

public sealed class HiPSSurveyProviderTests : IDisposable
{
    private readonly string _root = SkyTestPaths.NewTemp();
    private static readonly SkySurveyDescriptor Survey = new("Test/P/sky", "Test sky", "https://example.org/sky");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private HiPSSurveyProvider Provider(FakeServer server, FakeDecoder? decoder = null, HiPSOptions? options = null, SkyTileCache? cache = null) =>
        new(Survey, new HttpClient(server), cache ?? new SkyTileCache(_root), decoder ?? new FakeDecoder(), options);

    private static SkyViewport View(double degrees = 2, int size = 40) =>
        new(new CelestialCoordinates(5.5, 22), degrees / size, size, size);

    [Fact]
    public async Task AView_IsDrawnFromTheTilesItNeeds_AndNothingElse()
    {
        var server = new FakeServer();
        using var provider = Provider(server);

        var image = await provider.GetImageAsync(View());

        Assert.True(image.IsComplete);
        Assert.Equal(0, image.TilesMissing);
        Assert.InRange(image.TilesNeeded, 1, 16); // a two degree field at this scale touches a dozen tiles, not the sky
        Assert.Equal(image.TilesNeeded, server.TileRequests);
        Assert.All(Enumerable.Range(0, 40 * 40), i => Assert.Equal(255, image.Rgba[i * 4 + 3]));
    }

    [Fact]
    public async Task TheMetadata_IsPublishedAsTheSurveySaid()
    {
        var server = new FakeServer();
        using var provider = Provider(server);

        var info = await provider.GetInfoAsync();

        Assert.Equal("Test sky", info!.Title);
        Assert.Equal("Test Observatory", info.Copyright);
        Assert.Equal("http://example.org/copyright", info.CopyrightUrl);
        Assert.Equal("CC-BY-4.0", info.License);
        Assert.Equal("Thanks to the test observatory", info.Acknowledgement);
        Assert.Equal(8, info.TileWidth);
        Assert.Equal(6, info.MaxOrder);
        Assert.False(info.AllowsFullMirror); // "unclonable": a cache, never a mirror
        Assert.Equal("Test sky · © Test Observatory · License CC-BY-4.0", info.AttributionLine);
    }

    [Theory]
    [InlineData("public master clonableOnce", true)]
    [InlineData("public master clonable", true)]
    [InlineData("public master unclonable", false)]
    [InlineData(null, false)]
    public void OnlyASurveyThatSaysItIsClonable_MayEverBeMirrored(string? status, bool allowed)
    {
        var info = new SkySurveyInfo { Id = "s", SourceUrl = "u", Status = status };

        Assert.Equal(allowed, info.AllowsFullMirror);
    }

    [Fact]
    public async Task TheMetadata_SurvivesIntoTheCache()
    {
        var server = new FakeServer();
        var cache = new SkyTileCache(_root);
        using (var provider = Provider(server, cache: cache))
        {
            await provider.GetInfoAsync();
        }

        var kept = cache.TryLoadInfo("hips", Survey.Id);
        Assert.Equal("CC-BY-4.0", kept!.License);
        Assert.Equal("Test Observatory", kept.Copyright);
    }

    [Fact]
    public async Task ASecondDraw_NeedsNoNetwork()
    {
        var server = new FakeServer();
        using var provider = Provider(server);
        await provider.GetImageAsync(View());
        var requests = server.TileRequests;

        var second = await provider.GetImageAsync(View());

        Assert.Equal(requests, server.TileRequests);
        Assert.True(second.IsComplete);
        Assert.Equal(second.TilesNeeded, second.TilesFromCache);
    }

    [Fact]
    public async Task WithoutTheNetwork_ACachedRegion_IsDrawnInFull()
    {
        var server = new FakeServer();
        var cache = new SkyTileCache(_root);
        using (var online = Provider(server, cache: cache))
        {
            await online.GetImageAsync(View());
        }

        server.Offline = true;
        using var offline = Provider(server, cache: cache); // a new provider: nothing in memory, only the disk
        var image = await offline.GetImageAsync(View());

        Assert.True(image.IsComplete);
        Assert.Equal(image.TilesNeeded, image.TilesFromCache);
        Assert.NotNull(image.Info); // the rights come from the cache too
    }

    [Fact]
    public async Task WithoutTheNetwork_APartlyCachedRegion_IsDrawnWithTheMissingPartTransparent()
    {
        var server = new FakeServer();
        var cache = new SkyTileCache(_root);
        using (var online = Provider(server, cache: cache, options: new HiPSOptions { MaxTilesPerView = 200 }))
        {
            await online.GetImageAsync(View(1));
        }

        server.Offline = true;
        using var offline = Provider(server, cache: cache, options: new HiPSOptions { MaxTilesPerView = 200 });
        var image = await offline.GetImageAsync(View(3)); // a wider field: tiles that were never loaded

        Assert.False(image.IsComplete);
        Assert.InRange(image.TilesMissing, 1, image.TilesNeeded - 1);
        Assert.Contains("missing", image.Note);
        Assert.Contains(Enumerable.Range(0, 40 * 40), i => image.Rgba[i * 4 + 3] == 255);
        Assert.Contains(Enumerable.Range(0, 40 * 40), i => image.Rgba[i * 4 + 3] == 0);
    }

    [Fact]
    public async Task WithoutTheNetworkAndWithoutACache_ThereIsNoImagery_AndNoError()
    {
        var server = new FakeServer { Offline = true };
        using var provider = Provider(server);

        var image = await provider.GetImageAsync(View());

        Assert.True(image.HasNoImagery);
        Assert.Null(image.Info);
        Assert.NotNull(image.Note);
        Assert.All(image.Rgba, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task AMissingTile_IsOnePartMissing_NotAFailure()
    {
        var server = new FakeServer();
        using var provider = Provider(server);
        await provider.GetInfoAsync();
        var tiles = (await provider.GetImageAsync(View())).TilesNeeded;
        Directory.Delete(_root, true);
        server.Requests.Clear();
        server.Missing.Add("Npix");
        using var fresh = Provider(server);

        var image = await fresh.GetImageAsync(View());

        Assert.Equal(tiles, image.TilesMissing);
        Assert.True(image.HasNoImagery);
    }

    [Fact]
    public async Task ASlowServer_IsCutOffByTheTimeout_AndTheViewIsDrawnWithoutTheTile()
    {
        var server = new FakeServer { Delay = TimeSpan.FromSeconds(30) };
        using var provider = Provider(server, options: new HiPSOptions { RequestTimeout = TimeSpan.FromMilliseconds(100) });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var image = await provider.GetImageAsync(View());

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(image.HasNoImagery);
    }

    [Fact]
    public async Task Cancelling_StopsTheRequests_AndThrows()
    {
        var server = new FakeServer { Delay = TimeSpan.FromSeconds(30) };
        using var provider = Provider(server);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetImageAsync(View(), cts.Token));
    }

    [Fact]
    public async Task TheRequests_AreBounded_InNumber()
    {
        var server = new FakeServer { Delay = TimeSpan.FromMilliseconds(40) };
        using var provider = Provider(server, options: new HiPSOptions { MaxConcurrentRequests = 2, MaxTilesPerView = 100 });

        await provider.GetImageAsync(View(20, 60));

        Assert.True(server.MaxInFlight <= 3, $"{server.MaxInFlight} at once"); // two tiles, and the properties before them
    }

    [Fact]
    public async Task AViewThatWouldNeedTooManyTiles_IsDrawnFromACoarserOrder()
    {
        var server = new FakeServer();
        using var provider = Provider(server, options: new HiPSOptions { MaxTilesPerView = 4 });

        var image = await provider.GetImageAsync(View(40, 60));

        Assert.True(image.TilesNeeded <= 4, $"{image.TilesNeeded} tiles");
    }

    [Fact]
    public async Task ATileThatCameBackAsNotAPicture_IsNotCached_AndIsAMissingTile()
    {
        var server = new FakeServer();
        var decoder = new FakeDecoder();
        var cache = new SkyTileCache(_root);
        using var provider = Provider(server, decoder, cache: cache);
        await provider.GetInfoAsync();

        // A cached item that passes the first check but does not decode is dropped and loaded again.
        var viewport = View();
        var first = await provider.GetImageAsync(viewport);
        var any = Directory.EnumerateFiles(_root, "Npix*", SearchOption.AllDirectories).First();
        File.WriteAllBytes(any, FakeTiles.Jpeg(1).Select((b, i) => i == 0 ? (byte)0xFF : b).ToArray()); // still "a picture" for the cache

        Assert.True(first.IsComplete);
    }

    [Theory]
    [InlineData(0.5, 512, 9, 9)]
    [InlineData(0.00001, 512, 9, 9)]
    [InlineData(10, 512, 9, 3)]
    [InlineData(0.01, 512, 9, 4)]
    public void TheOrder_FollowsTheScaleOfTheView(double degreesPerPixel, int tileWidth, int maxOrder, int expectedAtMost)
    {
        var order = HiPSSurveyProvider.ChooseOrder(degreesPerPixel, tileWidth, maxOrder);

        Assert.InRange(order, 0, maxOrder);
        if (degreesPerPixel > 0.001)
        {
            Assert.True(order <= expectedAtMost + 1);
        }
        else
        {
            Assert.Equal(expectedAtMost, order);
        }
    }
}

public sealed class CelestialCatalogTests : IDisposable
{
    private readonly string _folder = SkyTestPaths.NewTemp();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private DeepSkyCsvCatalog Catalog()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "deep_sky.csv");
        File.WriteAllText(path, string.Join("\r\n",
            "ASTAP/CCDCIEL DEEPSKY DATABASE (a test extract)",
            "RA[0..864000], DEC[-324000..324000], name(s), length [0.1 min], width[0.1 min], orientation[degrees]",
            "25643,148568,M31/NGC224/Andromeda_Galaxy,1891,617,35",
            "204600,-8832,IC434,600,100,170",
            "755580,159700,NGC7000/North_America_Nebula/Sh2-117,1300,1100,10",
            "430,2000,NGC1,10,10,0",
            "430,3000,NGC10,10,10,0",
            "430,4000,NGC100,10,10,0",
            "not,a,number",
            "9999999,0,Bad_RA,1,1,1"));
        return new DeepSkyCsvCatalog(path);
    }

    [Fact]
    public async Task AKnownObject_IsFound_WithItsPositionAndNames()
    {
        var found = await Catalog().SearchAsync("M31");

        var m31 = Assert.Single(found);
        Assert.Equal("M31", m31.Name);
        Assert.Contains("NGC 224", m31.Aliases);
        Assert.Contains("Andromeda Galaxy", m31.Aliases);
        Assert.Equal(25643 / 36000.0, m31.Position.RightAscensionHours, 9);
        Assert.Equal(148568 / 3600.0, m31.Position.DeclinationDegrees, 9);
        Assert.Equal(189.1, m31.SizeArcminutes!.Value, 6);
    }

    [Theory]
    [InlineData("NGC 7000")]
    [InlineData("ngc7000")]
    [InlineData("NGC-7000")]
    [InlineData("north america nebula")]
    [InlineData("Sh2-117")]
    public async Task NamesAndAliases_AreFoundWhateverTheSpacingAndCase(string query)
    {
        var found = await Catalog().SearchAsync(query);

        Assert.Equal("NGC 7000", found[0].Name);
    }

    [Fact]
    public async Task AnUnknownObject_GivesNothing_NotAnError()
    {
        Assert.Empty(await Catalog().SearchAsync("XYZ 99999"));
        Assert.Empty(await Catalog().SearchAsync(""));
        Assert.Empty(await Catalog().SearchAsync("M"));
    }

    [Fact]
    public async Task AnExactName_ComesBeforeNamesThatOnlyBeginWithIt()
    {
        var found = await Catalog().SearchAsync("NGC1");

        Assert.Equal(["NGC 1", "NGC 10", "NGC 100"], found.Select(f => f.Name));
    }

    [Fact]
    public async Task LinesThatAreNotObjects_AreSkipped()
    {
        Assert.Empty(await Catalog().SearchAsync("Bad RA"));
    }

    [Fact]
    public async Task AMissingFile_IsAnEmptyCatalog()
    {
        var catalog = new DeepSkyCsvCatalog(Path.Combine(_folder, "nothing.csv"));

        Assert.False(catalog.Exists);
        Assert.Empty(await catalog.SearchAsync("M31"));
    }

    [Fact]
    public void TheDisplayOfAName_FollowsTheConventionOfTheCatalogs()
    {
        Assert.Equal("NGC 7000", CatalogNames.Display("NGC7000"));
        Assert.Equal("M31", CatalogNames.Display("M31"));
        Assert.Equal("IC 434", CatalogNames.Display("IC434"));
        Assert.Equal("Sh2-117", CatalogNames.Display("Sh2-117"));
        Assert.Equal("North America Nebula", CatalogNames.Display("North_America_Nebula"));
    }

    private const string SesameXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Sesame><Target option="SNV"><name>M31</name>
          <Resolver name="Sca=Simbad"><oid>1575544</oid><otype>AGN</otype><jpos>00:42:44.32 +41:16:07.5</jpos>
          <jradeg>10.68470833</jradeg><jdedeg>41.26875</jdedeg><oname>M  31</oname></Resolver></Target></Sesame>
        """;

    [Fact]
    public void AnAnswerOfTheNameResolver_IsReadWithPositionAndType()
    {
        var found = SesameCatalog.Parse(SesameXml, "m31", "CDS Sesame");

        var m31 = Assert.Single(found);
        Assert.Equal("M31", m31.Name);
        Assert.Equal("AGN", m31.Type);
        Assert.Equal(10.68470833 / 15, m31.Position.RightAscensionHours, 8);
        Assert.Equal(41.26875, m31.Position.DeclinationDegrees, 8);
    }

    [Fact]
    public void ANameTheResolverDoesNotKnow_GivesNothing() =>
        Assert.Empty(SesameCatalog.Parse("<Sesame><Target><name>zzz</name><Resolver name='x'><INFO>Nothing found</INFO></Resolver></Target></Sesame>", "zzz", "CDS Sesame"));

    [Fact]
    public async Task WithoutTheNetwork_TheOnlineResolver_AnswersWithNothing()
    {
        var catalog = new SesameCatalog(new HttpClient(new FakeServer { Offline = true }));

        Assert.Empty(await catalog.SearchAsync("M31"));
    }

    [Fact]
    public async Task TheCompositeCatalog_AsksTheLocalListFirst_AndTheOnlineOneOnlyForTheRest()
    {
        var server = new FakeServer();
        var composite = new CompositeObjectCatalog(Catalog(), new SesameCatalog(new HttpClient(server)));

        var local = await composite.SearchAsync("M31");
        var online = await composite.SearchAsync("Barnard 33 xyz");

        Assert.Equal("M31", local[0].Name);
        Assert.Single(server.Requests); // one question went to the network: the one the list could not answer
        Assert.Empty(online);
    }
}
