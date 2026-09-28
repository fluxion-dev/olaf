using Olaf.Core;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

/// <summary>
/// Issue #72 R1: <c>CopyrightScraper.Extract</c> vectors — year-anchored
/// positives, FSF/Apache/AAL-template negatives, placeholder negatives,
/// multi-holder, no-copyright, caps/dedup/length. Pure offline (no
/// <c>HttpClient</c>, no network); inline strings only, no fixture files.
/// </summary>
public sealed class CopyrightScraperTests
{
    [Fact]
    public void Should_ExtractHolder_When_CopyrightCForm()
    {
        var holders = CopyrightScraper.Extract("MIT License\nCopyright (c) 2024 Acme Corp\nPermission is hereby granted.");

        Assert.Equal(new[] { "Acme Corp" }, holders);
    }

    [Fact]
    public void Should_ExtractHolder_When_CopyrightSymbolForm()
    {
        var holders = CopyrightScraper.Extract("© 2023 Jane Doe");

        Assert.Equal(new[] { "Jane Doe" }, holders);
    }

    [Fact]
    public void Should_ExtractHolder_When_BareCForm()
    {
        var holders = CopyrightScraper.Extract("(c) 2020 Foo Bar");

        Assert.Equal(new[] { "Foo Bar" }, holders);
    }

    [Fact]
    public void Should_ExtractHolder_When_CopyrightSymbolWordForm()
    {
        var holders = CopyrightScraper.Extract("Copyright © 2021 Baz Inc. All rights reserved.");

        Assert.Equal(new[] { "Baz Inc" }, holders);
    }

    [Fact]
    public void Should_DenyTemplateHolders_When_FsfText()
    {
        var holders = CopyrightScraper.Extract("Copyright (C) 1989, 1991 Free Software Foundation, Inc.");

        Assert.Null(holders);
    }

    [Fact]
    public void Should_DenyTemplateHolders_When_ApacheAndAalTexts()
    {
        Assert.Null(CopyrightScraper.Extract("Copyright 2020 The Apache Software Foundation"));
        Assert.Null(CopyrightScraper.Extract("Copyright (c) 2002-2007 The Attribution Assurance Licenses Authors."));
    }

    [Fact]
    public void Should_DenyPlaceholders_When_TemplateJunk()
    {
        Assert.Null(CopyrightScraper.Extract("Copyright (c) <year> <copyright holders>"));
        Assert.Null(CopyrightScraper.Extract("Copyright (c) XXXX Gnomovision"));
        Assert.Null(CopyrightScraper.Extract("Copyright (c) 2024 Yoyodyne, Inc."));
    }

    [Fact]
    public void Should_ExtractAllHolders_When_MultiHolderText()
    {
        var text = "Copyright (c) 2024 Acme Corp\nSome other line.\nCopyright (c) 2019-2023 Beta LLC. All rights reserved.";

        var holders = CopyrightScraper.Extract(text);

        Assert.Equal(new[] { "Acme Corp", "Beta LLC" }, holders);
    }

    [Fact]
    public void Should_ReturnNull_When_NoCopyrightNotice()
    {
        Assert.Null(CopyrightScraper.Extract("MIT License\nPermission is hereby granted, free of charge."));
        Assert.Null(CopyrightScraper.Extract(null));
        Assert.Null(CopyrightScraper.Extract("   "));
    }

    [Fact]
    public void Should_EnforceCaps_When_ManyDuplicatesAndLongHolder()
    {
        var longHolder = new string('A', 250);
        var text = string.Join(
            '\n',
            "Copyright (c) 2024 Acme Corp",
            "Copyright (c) 2023 ACME CORP",
            "Copyright (c) 2022 Beta LLC",
            "Copyright (c) 2021 Gamma Inc",
            "Copyright (c) 2020 Delta Co",
            "Copyright (c) 2019 " + longHolder,
            "Copyright (c) 2018 Epsilon Ltd",
            "Copyright (c) 2017 Zeta GmbH");

        var holders = CopyrightScraper.Extract(text);

        Assert.NotNull(holders);
        Assert.Equal(5, holders.Length);
        Assert.Equal("Acme Corp", holders[0]);
        Assert.Equal(200, holders[4].Length);
        Assert.DoesNotContain("Epsilon Ltd", holders);
        Assert.DoesNotContain("Zeta GmbH", holders);
    }

    [Fact]
    public void Should_ReturnNull_When_CopyrightHasNoYear()
    {
        // Year anchor is structural (B1): a notice without a year is not a
        // holder, even with a plausible name attached.
        Assert.Null(CopyrightScraper.Extract("Copyright (c) Acme Corp"));
        Assert.Null(CopyrightScraper.Extract("Copyright Acme Corp. All rights reserved."));
        Assert.Null(CopyrightScraper.Extract("(c) Foo Bar"));
    }

    [Fact]
    public void Should_StripByPrefix_When_HolderStartsWithBy()
    {
        var holders = CopyrightScraper.Extract("Copyright (c) 2024 by Acme Corp");

        Assert.Equal(new[] { "Acme Corp" }, holders);
    }

    [Fact]
    public void Should_DenyBareContributors_When_CatchAllHolder()
    {
        Assert.Null(CopyrightScraper.Extract("Copyright (c) 2024 contributors"));
        Assert.Null(CopyrightScraper.Extract("Copyright (c) 2024 the contributors."));
    }

    [Fact]
    public void Should_PromoteDisplayName_When_AuthorHasEmail()
    {
        // "Name <mail>" promotes the display name only; bare mails and
        // blanks never promote (the supplier keeps the #70 email fallback).
        Assert.Equal(
            new[] { "Ada Lovelace" },
            CopyrightScraper.FromAuthorFallback("Ada Lovelace <ada@example.com>"));
        Assert.Null(CopyrightScraper.FromAuthorFallback("bot@example.com"));
        Assert.Null(CopyrightScraper.FromAuthorFallback(null));
        Assert.Null(CopyrightScraper.FromAuthorFallback("   "));
    }

    [Fact]
    public void Should_PreferScrapedText_When_ResolvingHolders()
    {
        // Scraped-text-first even when the author differs; the author
        // fallback applies only when the per-package text yields zero
        // (null text = non-per-package DB source, never scraped).
        Assert.Equal(
            new[] { "Acme Corp" },
            CopyrightScraper.ResolveHolders("Copyright (c) 2024 Acme Corp", "Registry Author"));
        Assert.Equal(new[] { "Ada Lovelace" }, CopyrightScraper.ResolveHolders(null, "Ada Lovelace"));
        Assert.Null(CopyrightScraper.ResolveHolders("MIT License\nPermission is hereby granted.", null));
    }

    [Fact]
    public void Should_PreserveSupplier_When_AttachingHolders()
    {
        var dependency = new Dependency("npm", "acme", "1.0.0", false);

        // Null enrichment + holders builds via helpers (purl flows);
        // existing enrichment is extended with holders, supplier untouched;
        // null holders return the enrichment untouched (same instance).
        var built = CopyrightScraper.AttachHolders(dependency, null, "Copyright (c) 2024 Acme Corp", null);

        Assert.NotNull(built);
        Assert.Equal(new[] { "Acme Corp" }, built.CopyrightHolders);
        Assert.Equal("pkg:npm/acme@1.0.0", built.Purl);

        var existing = new Enrichment("pkg:npm/acme@1.0.0", null, "Registry Author", null, null);
        var extended = CopyrightScraper.AttachHolders(dependency, existing, "Copyright (c) 2024 Acme Corp", "Other Author");

        Assert.NotNull(extended);
        Assert.Equal("Registry Author", extended.Supplier);
        Assert.Equal(new[] { "Acme Corp" }, extended.CopyrightHolders);

        Assert.Same(existing, CopyrightScraper.AttachHolders(dependency, existing, null, null));
    }
}
