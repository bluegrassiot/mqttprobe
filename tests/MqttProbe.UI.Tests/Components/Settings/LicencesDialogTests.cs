using Microsoft.Extensions.DependencyInjection;
using MqttProbe.UI.Components.Settings;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Settings;

[TestFixture]
public class LicencesDialogTests : BunitTestContext
{
    private static readonly string _documentA = new('a', 64);
    private static readonly string _documentB = new('b', 64);
    private static readonly string _documentC = new('c', 64);

    private const string AppLicenseText =
        "Apache License\nVersion 2.0\n\nLicensed under the Apache License.\n";

    private IRenderedComponent<MudDialogProvider> _provider = null!;

    [SetUp]
    public void Setup()
    {
        EnsureMudProviders();
        _provider = Render<MudDialogProvider>();
    }

    [Test]
    public async Task Opens_With_The_App_Licence_Available()
    {
        await OpenAsync();

        _provider.Find(".licences-dialog__title").TextContent.Trim().Should().Be("Open-source licences");
        _provider.Find("[data-testid=app-licence-button]").TextContent.Should().Contain("MQTTProbe licence");
        _provider.Find("[data-testid=licence-detail-title]").TextContent.Trim().Should().Be("MQTTProbe licence");
        _provider.Find("[data-testid=licence-document-text]").TextContent.Should().Contain("Apache License");
    }

    [Test]
    public async Task Search_Matches_Name_Licence_Version_And_Copyright_Case_Insensitively()
    {
        await OpenAsync();

        var expectations = new (string Query, string Expected)[] { ("ALPHA", "Alpha Core"), ("mit", "Alpha Core"), ("4.5", "Beta Widget"), ("2023 beta", "Beta Widget"), ("1.2", "Alpha Core") };
        foreach (var (query, expected) in expectations)
        {
            _provider.Find("[data-testid=licence-search]").Input(query);

            _provider.WaitForAssertion(() =>
            {
                var rows = _provider.FindAll("[data-testid=licence-component]");
                rows.Should().ContainSingle();
                rows[0].TextContent.Should().Contain(expected);
                _provider.Find("[data-testid=licence-count]").TextContent.Trim().Should().Be("Showing 1 of 3 components");
            });
        }
    }

    [Test]
    public async Task Search_Can_Be_Cleared_And_No_Results_Is_Visible()
    {
        await OpenAsync();

        _provider.Find("[data-testid=licence-search]").Input("no such component");

        _provider.WaitForAssertion(() =>
        {
            _provider.FindAll("[data-testid=licence-component]").Should().BeEmpty();
            _provider.Find("[data-testid=licence-no-results]").TextContent.Should().Contain("No components match your search.");
            _provider.Find("[data-testid=licence-count]").TextContent.Trim().Should().Be("Showing 0 of 3 components");
        });

        _provider.Find("[data-testid=licence-clear-search]").Click();

        _provider.WaitForAssertion(() =>
        {
            _provider.FindAll("[data-testid=licence-component]").Should().HaveCount(3);
            _provider.Find("[data-testid=licence-count]").TextContent.Trim().Should().Be("3 components");
            _provider.FindAll("[data-testid=licence-no-results]").Should().BeEmpty();
        });
    }

    [Test]
    public async Task Selecting_A_Component_Shows_Metadata_Source_And_All_Linked_Texts()
    {
        await OpenAsync();

        SelectComponent("Alpha Core");

        _provider.WaitForAssertion(() =>
        {
            _provider.Find("[data-testid=licence-detail-title]").TextContent.Trim().Should().Be("Alpha Core");
            _provider.Find(".licences-dialog__meta").TextContent.Should().Contain("MIT");
            _provider.Find(".licences-dialog__meta").TextContent.Should().Contain("1.2.3");
            _provider.Find(".licences-dialog__meta").TextContent.Should().Contain("Copyright 2024 Alpha");

            var source = _provider.Find("[data-testid=licence-source-link]");
            source.GetAttribute("href").Should().Be("https://example.com/alpha");
            source.GetAttribute("target").Should().Be("_blank");
            source.GetAttribute("rel").Should().Be("noopener noreferrer");
            source.GetAttribute("aria-label").Should().Contain("opens in a new tab");

            var texts = _provider.FindAll("[data-testid=licence-document-text]");
            texts.Should().HaveCount(2);
            texts[0].TextContent.Should().Contain("MIT License");
            texts[1].TextContent.Should().Contain("Apache License");
        });
    }

    [Test]
    public async Task Switching_Components_Replaces_Text_And_Reuses_Shared_Documents()
    {
        await OpenAsync();

        SelectComponent("Alpha Core");
        _provider.WaitForAssertion(() =>
            _provider.FindAll("[data-testid=licence-document-text]").Select(text => text.TextContent)
                .Should().Contain(text => text.Contains("Apache License")));

        SelectComponent("Beta Widget");
        _provider.WaitForAssertion(() =>
        {
            _provider.Find("[data-testid=licence-detail-title]").TextContent.Trim().Should().Be("Beta Widget");
            var texts = _provider.FindAll("[data-testid=licence-document-text]");
            texts.Should().ContainSingle();
            texts[0].TextContent.Should().Contain("Apache License");
            texts[0].TextContent.Should().NotContain("MIT License");
        });

        SelectComponent("Gamma Kit");
        _provider.WaitForAssertion(() =>
        {
            var texts = _provider.FindAll("[data-testid=licence-document-text]");
            texts.Should().ContainSingle();
            texts[0].TextContent.Should().Contain("BSD 3-Clause");
            texts[0].TextContent.Should().NotContain("Apache License");
        });
    }

    [Test]
    public async Task Shared_Document_Text_Is_Loaded_Once_Across_Components()
    {
        var resources = Resources();
        var reads = new List<string>();
        var reader = new LegalCatalogueReader(name =>
        {
            if (name.StartsWith("MqttProbe.UI.Legal.Texts.", StringComparison.Ordinal))
                reads.Add(name);
            return resources.GetValueOrDefault(name);
        });

        await OpenAsync(reader);
        SelectComponent("Alpha Core");
        SelectComponent("Beta Widget");
        SelectComponent("Alpha Core");

        reads.Count(name => name == LegalCatalogueReader.DocumentResourceName(_documentB)).Should().Be(1);
    }

    [Test]
    public async Task The_App_Licence_Remains_Reachable_After_Selecting_A_Component()
    {
        await OpenAsync();
        SelectComponent("Alpha Core");

        _provider.Find("[data-testid=app-licence-button]").Click();

        _provider.WaitForAssertion(() =>
        {
            _provider.Find("[data-testid=licence-detail-title]").TextContent.Trim().Should().Be("MQTTProbe licence");
            _provider.Find("[data-testid=licence-document-text]").TextContent.Should().Contain("Licensed under the Apache License.");
        });
    }

    [Test]
    public async Task Back_Returns_To_The_Component_List_And_The_Document_Region_Is_Focusable()
    {
        await OpenAsync();

        var body = _provider.Find(".licences-dialog__body");
        body.GetAttribute("class").Should().NotContain("licences-dialog__body--detail");

        SelectComponent("Alpha Core");

        _provider.WaitForAssertion(() =>
        {
            _provider.Find(".licences-dialog__body").GetAttribute("class").Should().Contain("licences-dialog__body--detail");
            var region = _provider.Find(".licences-dialog__detail-scroll");
            region.GetAttribute("tabindex").Should().Be("0");
            region.GetAttribute("role").Should().Be("region");
            region.GetAttribute("aria-label").Should().Be("Alpha Core licence details");
        });

        _provider.Find("[data-testid=licence-back]").Click();

        _provider.WaitForAssertion(() =>
            _provider.Find(".licences-dialog__body").GetAttribute("class")
                .Should().NotContain("licences-dialog__body--detail"));
    }

    [Test]
    public async Task Missing_Catalogue_Shows_An_Error_And_Keeps_The_App_Licence_Available()
    {
        await OpenAsync(ReaderFor(new()
        {
            [LegalCatalogueReader.AppLicenseResourceName] = AppLicenseText
        }));

        _provider.Find("[data-testid=licence-catalogue-error]").TextContent
            .Should().Contain(LegalCatalogueReader.CatalogueErrorText);
        _provider.FindAll("[data-testid=licence-search]").Should().BeEmpty();
        _provider.Find("[data-testid=licence-document-text]").TextContent.Should().Contain("Apache License");
    }

    [Test]
    public async Task Missing_Document_Resource_Is_Reported_Instead_Of_Claiming_It_Is_Included()
    {
        var resources = Resources();
        resources.Remove(LegalCatalogueReader.DocumentResourceName(_documentB));

        await OpenAsync(ReaderFor(resources));
        SelectComponent("Beta Widget");

        _provider.WaitForAssertion(() =>
        {
            _provider.Find("[data-testid=licence-document-error]").TextContent
                .Should().Contain(LegalCatalogueReader.DocumentErrorText);
            _provider.FindAll("[data-testid=licence-document-text]").Should().BeEmpty();
        });
    }

    [Test]
    public async Task Missing_App_Licence_Is_Reported()
    {
        var resources = Resources();
        resources.Remove(LegalCatalogueReader.AppLicenseResourceName);

        await OpenAsync(ReaderFor(resources));

        _provider.Find("[data-testid=licence-document-error]").TextContent
            .Should().Contain(LegalCatalogueReader.AppLicenseErrorText);
    }

    [Test]
    public async Task Dialog_Is_Named_By_Its_Visible_Title()
    {
        await OpenAsync();

        var titleId = _provider.Find(".licences-dialog__title").GetAttribute("id");
        titleId.Should().NotBeNullOrEmpty();
        _provider.Find(".mud-dialog").GetAttribute("aria-labelledby").Should().Be(titleId);
    }

    [Test]
    public async Task Close_Button_Closes_The_Viewer()
    {
        await OpenAsync();

        _provider.Find("[data-testid=licences-close]").Click();

        _provider.WaitForAssertion(() =>
            _provider.FindAll(".licences-dialog__title").Should().BeEmpty());
    }

    private async Task OpenAsync(LegalCatalogueReader? reader = null)
    {
        var parameters = new DialogParameters<LicencesDialog>
        {
            { x => x.Reader, reader ?? ReaderFor(Resources()) }
        };
        var dialogService = Services.GetRequiredService<IDialogService>();

        await _provider.InvokeAsync(async () =>
            await dialogService.ShowAsync<LicencesDialog>(null, parameters));
    }

    private void SelectComponent(string name) =>
        _provider.FindAll("[data-testid=licence-component]")
            .Single(row => row.TextContent.Contains(name))
            .Click();

    private static LegalCatalogueReader ReaderFor(Dictionary<string, string> resources) =>
        new(name => resources.GetValueOrDefault(name));

    private static Dictionary<string, string> Resources() => new()
    {
        [LegalCatalogueReader.CatalogueResourceName] = CatalogueJson(),
        [LegalCatalogueReader.AppLicenseResourceName] = AppLicenseText,
        [LegalCatalogueReader.DocumentResourceName(_documentA)] = "MIT License\n\nPermission is hereby granted.\n",
        [LegalCatalogueReader.DocumentResourceName(_documentB)] = "Apache License\nVersion 2.0\n",
        [LegalCatalogueReader.DocumentResourceName(_documentC)] = "BSD 3-Clause License\n\nRedistribution and use.\n"
    };

    private static string CatalogueJson() => $$"""
        {
          "schemaVersion": 1,
          "components": [
            {
              "id": "alpha",
              "name": "Alpha Core",
              "version": "1.2.3",
              "license": "MIT",
              "copyright": "Copyright 2024 Alpha",
              "sourceUrl": "https://example.com/alpha",
              "documentIds": ["{{_documentA}}", "{{_documentB}}"]
            },
            {
              "id": "beta",
              "name": "Beta Widget",
              "version": "4.5",
              "license": "Apache-2.0",
              "copyright": "Copyright 2023 Beta",
              "sourceUrl": "",
              "documentIds": ["{{_documentB}}"]
            },
            {
              "id": "gamma",
              "name": "Gamma Kit",
              "version": "2.0",
              "license": "BSD-3-Clause",
              "copyright": "Copyright 2022 Gamma",
              "sourceUrl": "",
              "documentIds": ["{{_documentC}}"]
            }
          ],
          "documents": [
            { "id": "{{_documentA}}", "title": "MIT Licence", "file": "{{_documentA}}.txt", "sha256": "{{_documentA}}", "sourceUrls": [] },
            { "id": "{{_documentB}}", "title": "Apache Licence", "file": "{{_documentB}}.txt", "sha256": "{{_documentB}}", "sourceUrls": [] },
            { "id": "{{_documentC}}", "title": "BSD Licence", "file": "{{_documentC}}.txt", "sha256": "{{_documentC}}", "sourceUrls": [] }
          ]
        }
        """;
}
