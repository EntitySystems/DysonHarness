using System.IO.Compression;
using System.Net;
using System.Text;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: explorer routing + Skills Directory search/preview/extract with mocked HTTP.
/// </summary>
public class DysonSkillExplorerTests
{
    [Fact]
    public void ListProviders_preserves_registration_order_skillshub_skillssh_clawhub_skillsdirectory()
    {
        using var httpHub = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("https://skillshub.wtf/"),
        };
        using var httpSh = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("https://skills.sh/"),
        };
        using var httpClaw = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("https://clawhub.ai/"),
        };
        using var httpSd = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("https://www.skillsdirectory.com/"),
        };

        var explorer = new DysonSkillExplorer(
        [
            new SkillsHubSkillExplorerProvider(httpHub),
            new SkillsShSkillExplorerProvider(httpSh),
            new ClawHubSkillExplorerProvider(httpClaw),
            new SkillsDirectorySkillExplorerProvider(httpSd),
        ]);

        var providers = explorer.ListProviders();
        Assert.Equal(4, providers.Count);
        Assert.Equal(
            [
                SkillsHubSkillExplorerProvider.ProviderId,
                SkillsShSkillExplorerProvider.ProviderId,
                ClawHubSkillExplorerProvider.ProviderId,
                SkillsDirectorySkillExplorerProvider.ProviderId,
            ],
            providers.Select(p => p.Name).ToArray());
        Assert.Equal(
            [
                SkillsHubSkillExplorerProvider.ProviderDisplayName,
                SkillsShSkillExplorerProvider.ProviderDisplayName,
                ClawHubSkillExplorerProvider.ProviderDisplayName,
                SkillsDirectorySkillExplorerProvider.ProviderDisplayName,
            ],
            providers.Select(p => p.DisplayName).ToArray());
    }

    [Fact]
    public async Task Explorer_routes_case_insensitively_and_rejects_unknown()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"skills":[],"pagination":{"total":0,"limit":10,"offset":0,"hasMore":false}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.skillsdirectory.com/") };
        var provider = new SkillsDirectorySkillExplorerProvider(http);
        var explorer = new DysonSkillExplorer([provider]);

        var providers = explorer.ListProviders();
        Assert.Single(providers);
        Assert.Equal(SkillsDirectorySkillExplorerProvider.ProviderId, providers[0].Name);
        Assert.Equal(SkillsDirectorySkillExplorerProvider.ProviderDisplayName, providers[0].DisplayName);

        var ok = await explorer.SearchAsync("SkillsDirectory", "git", limit: 10, offset: 0);
        Assert.True(ok.IsSuccess, ok.IsError ? ok.Error : null);

        var unknown = await explorer.SearchAsync("nope", "git", limit: 10, offset: 0);
        Assert.True(unknown.IsError);
        Assert.Contains("Unknown skill explorer provider", unknown.Error, StringComparison.OrdinalIgnoreCase);

        var empty = await explorer.GetAsync("  ", "x");
        Assert.True(empty.IsError);
        Assert.Contains("providerName is required", empty.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkillsDirectory_search_maps_registry_page()
    {
        var handler = new StubHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/api/registry", req.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("q=git", req.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("limit=10", req.RequestUri.Query, StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "skills": [
                        {
                          "name": "Git Commit Helper",
                          "slug": "git-commit-helper",
                          "description": "Better commits",
                          "author": "alice",
                          "stars": 42,
                          "verified": true,
                          "tags": ["git", "commits"]
                        }
                      ],
                      "pagination": { "total": 1, "limit": 10, "offset": 0, "hasMore": false }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.skillsdirectory.com/") };
        var provider = new SkillsDirectorySkillExplorerProvider(http);

        var page = await provider.SearchAsync("git", limit: 10, offset: 0);
        Assert.True(page.IsSuccess, page.IsError ? page.Error : null);
        Assert.Equal(1, page.Value.Total);
        Assert.False(page.Value.HasMore);
        Assert.Single(page.Value.Skills);
        Assert.Equal("git-commit-helper", page.Value.Skills[0].Slug);
        Assert.Equal("alice", page.Value.Skills[0].Author);
        Assert.Equal(42, page.Value.Skills[0].Stars);
        Assert.True(page.Value.Skills[0].Verified);
        Assert.Equal(["git", "commits"], page.Value.Skills[0].Tags);
    }

    [Fact]
    public async Task SkillsDirectory_get_maps_object_author_and_github_stars()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "skill": {
                    "name": "Memory Curator",
                    "slug": "memory-curator",
                    "description": "Manages memory",
                    "author": { "name": "markmdev", "url": "https://github.com/markmdev" },
                    "github": { "stars": 127 },
                    "verified": true,
                    "tags": ["memory"]
                  }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.skillsdirectory.com/") };
        var provider = new SkillsDirectorySkillExplorerProvider(http);

        var entry = await provider.GetAsync("memory-curator");
        Assert.True(entry.IsSuccess, entry.IsError ? entry.Error : null);
        Assert.Equal("memory-curator", entry.Value.Slug);
        Assert.Equal("markmdev", entry.Value.Author);
        Assert.Equal(127, entry.Value.Stars);
    }

    [Fact]
    public async Task SkillsDirectory_preview_and_download_use_site_zip()
    {
        var zipBytes = BuildSkillZip(
            rootFolder: "memory-curator",
            skillMarkdown: "# Memory Curator\n\nHello.",
            extraRelativePath: "notes.md",
            extraContents: "extra");

        var handler = new StubHandler(req =>
        {
            Assert.Contains("/api/skills/memory-curator/download", req.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zipBytes)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip") },
                },
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.skillsdirectory.com/") };
        var provider = new SkillsDirectorySkillExplorerProvider(http);

        var preview = await provider.PreviewSkillMarkdownAsync("memory-curator");
        Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
        var previewMd = Assert.IsType<DysonSkillExplorerPreviewOutcome.Markdown>(preview.Value);
        Assert.Contains("Memory Curator", previewMd.Content, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-explorer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var installed = await provider.DownloadAsync("memory-curator", fs);
            Assert.True(installed.IsSuccess, installed.IsError ? installed.Error : null);
            var installedPath = Assert.IsType<DysonSkillExplorerDownloadOutcome.Installed>(installed.Value);
            Assert.Equal(".dyson/skills/memory-curator", installedPath.RelativePath.Replace('\\', '/'));

            var skillMd = fs.ReadAllText(".dyson/skills/memory-curator/SKILL.md");
            Assert.True(skillMd.IsSuccess, skillMd.IsError ? skillMd.Error : null);
            Assert.Contains("Hello.", skillMd.Value, StringComparison.Ordinal);

            var notes = fs.ReadAllText(".dyson/skills/memory-curator/notes.md");
            Assert.True(notes.IsSuccess, notes.IsError ? notes.Error : null);
            Assert.Equal("extra", notes.Value);

            // overwrite same slug
            var zip2 = BuildSkillZip(
                rootFolder: "memory-curator",
                skillMarkdown: "# v2",
                extraRelativePath: null,
                extraContents: null);
            handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zip2),
            };
            var again = await provider.DownloadAsync("memory-curator", fs);
            Assert.True(again.IsSuccess, again.IsError ? again.Error : null);
            var v2 = fs.ReadAllText(".dyson/skills/memory-curator/SKILL.md");
            Assert.True(v2.IsSuccess, v2.IsError ? v2.Error : null);
            Assert.Equal("# v2", v2.Value.Trim());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SkillsDirectory_rejects_invalid_slug_and_missing_skill_md()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("https://www.skillsdirectory.com/"),
        };
        var provider = new SkillsDirectorySkillExplorerProvider(http);

        var badSlug = await provider.PreviewSkillMarkdownAsync("../evil");
        Assert.True(badSlug.IsError);
        Assert.Contains("slug", badSlug.Error, StringComparison.OrdinalIgnoreCase);

        var emptyZip = BuildZipWithEntry("readme.txt", "no skill");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(emptyZip),
        });
        using var http2 = new HttpClient(handler) { BaseAddress = new Uri("https://www.skillsdirectory.com/") };
        var provider2 = new SkillsDirectorySkillExplorerProvider(http2);

        var missing = await provider2.PreviewSkillMarkdownAsync("no-skill-md");
        Assert.True(missing.IsError);
        Assert.Contains("SKILL.md", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkillsDirectory_download_http_error_surfaces_status()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("missing"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.skillsdirectory.com/") };
        var provider = new SkillsDirectorySkillExplorerProvider(http);

        var preview = await provider.PreviewSkillMarkdownAsync("missing-skill");
        Assert.True(preview.IsError);
        Assert.Contains("404", preview.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractZip_strips_single_root_and_rejects_traversal()
    {
        var good = BuildSkillZip("pkg", "# ok", null, null);
        using var goodMs = new MemoryStream(good);
        using var goodZip = new ZipArchive(goodMs, ZipArchiveMode.Read);
        Assert.Equal("pkg/", DysonSkillPackageInstall.DetectSingleRootPrefix(goodZip));

        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var extracted = DysonSkillPackageInstall.ExtractZipToSkillDir(good, "pkg", fs);
            Assert.True(extracted.IsSuccess, extracted.IsError ? extracted.Error : null);

            var evilBytes = BuildZipWithEntry("../escape/SKILL.md", "# no");
            var evil = DysonSkillPackageInstall.ExtractZipToSkillDir(evilBytes, "evil", fs);
            Assert.True(evil.IsError);
            Assert.Contains("unsafe", evil.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ClawHub_search_maps_owner_composite_slug()
    {
        var handler = new StubHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/api/v1/search", req.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("q=git", req.RequestUri.Query, StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "results": [
                        {
                          "slug": "git",
                          "displayName": "Git",
                          "summary": "Commits and branches",
                          "ownerHandle": "ivangdavila",
                          "downloads": 17086,
                          "official": false,
                          "owner": { "handle": "ivangdavila", "displayName": "Ivan" },
                          "native": {
                            "skill": {
                              "stats": { "stars": 31, "downloads": 17086 },
                              "topics": ["Git"]
                            }
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://clawhub.ai/") };
        var provider = new ClawHubSkillExplorerProvider(http);

        var page = await provider.SearchAsync("git", limit: 10, offset: 0);
        Assert.True(page.IsSuccess, page.IsError ? page.Error : null);
        Assert.Single(page.Value.Skills);
        Assert.Equal("ivangdavila/git", page.Value.Skills[0].Slug);
        Assert.Equal("Ivan", page.Value.Skills[0].Author);
        Assert.Equal(31, page.Value.Skills[0].Stars);
        Assert.Equal(["Git"], page.Value.Skills[0].Tags);
    }

    [Fact]
    public async Task ClawHub_empty_query_browses_downloads_first_page_only()
    {
        var calls = 0;
        var handler = new StubHandler(req =>
        {
            calls++;
            Assert.Contains("/api/v1/skills", req.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("sort=downloads", req.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("limit=5", req.RequestUri.Query, StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "items": [
                        {
                          "slug": "self-improving-agent",
                          "displayName": "self-improving agent",
                          "summary": "Learns",
                          "ownerHandle": "clawd",
                          "owner": { "handle": "clawd", "displayName": "Clawd" },
                          "topics": ["self-improvement"],
                          "stats": { "stars": 3957, "downloads": 471223 }
                        }
                      ],
                      "nextCursor": "opaque"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://clawhub.ai/") };
        var provider = new ClawHubSkillExplorerProvider(http);

        var page = await provider.SearchAsync(null, limit: 5, offset: 0);
        Assert.True(page.IsSuccess, page.IsError ? page.Error : null);
        Assert.Equal(1, calls);
        Assert.Single(page.Value.Skills);
        Assert.Equal("clawd/self-improving-agent", page.Value.Skills[0].Slug);
        Assert.Equal("Clawd", page.Value.Skills[0].Author);
        Assert.Equal(3957, page.Value.Skills[0].Stars);
        Assert.False(page.Value.HasMore);

        var page2 = await provider.SearchAsync("", limit: 5, offset: 5);
        Assert.True(page2.IsSuccess, page2.IsError ? page2.Error : null);
        Assert.Empty(page2.Value.Skills);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClawHub_preview_and_download_zip_use_ownerHandle()
    {
        var zipBytes = BuildSkillZip(
            rootFolder: "git",
            skillMarkdown: "# Git\n\nFrom ClawHub.",
            extraRelativePath: null,
            extraContents: null);

        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var query = req.RequestUri.Query;
            if (path.EndsWith("/file", StringComparison.Ordinal))
            {
                Assert.Contains("path=SKILL.md", query, StringComparison.Ordinal);
                Assert.Contains("ownerHandle=ivangdavila", query, StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("# Git\n\nFrom ClawHub.", Encoding.UTF8, "text/markdown"),
                };
            }

            Assert.Contains("/api/v1/download", path, StringComparison.Ordinal);
            Assert.Contains("slug=git", query, StringComparison.Ordinal);
            Assert.Contains("ownerHandle=ivangdavila", query, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zipBytes)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip") },
                },
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://clawhub.ai/") };
        var provider = new ClawHubSkillExplorerProvider(http);

        var preview = await provider.PreviewSkillMarkdownAsync("ivangdavila/git");
        Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
        var previewMd = Assert.IsType<DysonSkillExplorerPreviewOutcome.Markdown>(preview.Value);
        Assert.Contains("From ClawHub", previewMd.Content, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "dyson-clawhub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var installed = await provider.DownloadAsync("ivangdavila/git", fs);
            Assert.True(installed.IsSuccess, installed.IsError ? installed.Error : null);
            var installedPath = Assert.IsType<DysonSkillExplorerDownloadOutcome.Installed>(installed.Value);
            Assert.Equal(".dyson/skills/ivangdavila-git", installedPath.RelativePath.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ClawHub_409_ambiguous_slug_returns_matches_and_owner_retry_installs()
    {
        var zipBytes = BuildSkillZip(
            rootFolder: "skill-vetter",
            skillMarkdown: "# Skill Vetter\n\nOK.",
            extraRelativePath: null,
            extraContents: null);

        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var query = req.RequestUri.Query;

            if (path.Contains("/api/v1/download", StringComparison.Ordinal)
                && query.Contains("slug=skill-vetter", StringComparison.Ordinal)
                && !query.Contains("ownerHandle=", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(
                        """
                        {
                          "code": "AMBIGUOUS_SKILL_SLUG",
                          "message": "Multiple skills share this slug; specify ownerHandle.",
                          "slug": "skill-vetter",
                          "matches": [
                            {
                              "ownerHandle": "spclaudehome",
                              "slug": "skill-vetter",
                              "ref": "@spclaudehome/skill-vetter",
                              "url": "https://clawhub.ai/@spclaudehome/skill-vetter"
                            },
                            {
                              "ownerHandle": "otherpub",
                              "slug": "skill-vetter",
                              "ref": "@otherpub/skill-vetter",
                              "url": "https://clawhub.ai/@otherpub/skill-vetter"
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (path.Contains("/api/v1/download", StringComparison.Ordinal)
                && query.Contains("slug=skill-vetter", StringComparison.Ordinal)
                && query.Contains("ownerHandle=spclaudehome", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zipBytes)
                    {
                        Headers =
                        {
                            ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip"),
                        },
                    },
                };
            }

            if (path.EndsWith("/file", StringComparison.Ordinal)
                && !query.Contains("ownerHandle=", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(
                        """
                        {
                          "code": "AMBIGUOUS_SKILL_SLUG",
                          "message": "Multiple skills share this slug; specify ownerHandle.",
                          "slug": "skill-vetter",
                          "matches": [
                            {
                              "ownerHandle": "spclaudehome",
                              "slug": "skill-vetter",
                              "ref": "@spclaudehome/skill-vetter",
                              "url": "https://clawhub.ai/@spclaudehome/skill-vetter"
                            },
                            {
                              "ownerHandle": "otherpub",
                              "slug": "skill-vetter",
                              "ref": "@otherpub/skill-vetter",
                              "url": "https://clawhub.ai/@otherpub/skill-vetter"
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            throw new InvalidOperationException("Unexpected request: " + req.RequestUri);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://clawhub.ai/") };
        var provider = new ClawHubSkillExplorerProvider(http);

        var preview = await provider.PreviewSkillMarkdownAsync("skill-vetter");
        Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
        var previewAmbiguous = Assert.IsType<DysonSkillExplorerPreviewOutcome.Ambiguous>(preview.Value);
        Assert.Equal(2, previewAmbiguous.Matches.Count);
        Assert.Equal("spclaudehome/skill-vetter", previewAmbiguous.Matches[0].Slug);
        Assert.Equal("@spclaudehome/skill-vetter", previewAmbiguous.Matches[0].Label);
        Assert.Equal("otherpub/skill-vetter", previewAmbiguous.Matches[1].Slug);
        Assert.Equal("@otherpub/skill-vetter", previewAmbiguous.Matches[1].Label);

        var root = Path.Combine(Path.GetTempPath(), "dyson-clawhub-409-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = DysonWorkspaceTestFs.CreateLocal(root);

            var ambiguous = await provider.DownloadAsync("skill-vetter", fs);
            Assert.True(ambiguous.IsSuccess, ambiguous.IsError ? ambiguous.Error : null);
            var downloadAmbiguous = Assert.IsType<DysonSkillExplorerDownloadOutcome.Ambiguous>(ambiguous.Value);
            Assert.Equal(2, downloadAmbiguous.Matches.Count);
            Assert.Equal("spclaudehome/skill-vetter", downloadAmbiguous.Matches[0].Slug);
            Assert.Equal("otherpub/skill-vetter", downloadAmbiguous.Matches[1].Slug);

            var installed = await provider.DownloadAsync("spclaudehome/skill-vetter", fs);
            Assert.True(installed.IsSuccess, installed.IsError ? installed.Error : null);
            var path = Assert.IsType<DysonSkillExplorerDownloadOutcome.Installed>(installed.Value);
            Assert.Equal(".dyson/skills/spclaudehome-skill-vetter", path.RelativePath.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ClawHub_409_legacy_error_field_still_parses_ambiguous()
    {
        var handler = new StubHandler(req =>
        {
            if (!req.RequestUri!.AbsolutePath.EndsWith("/file", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected request: " + req.RequestUri);

            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    """
                    {
                      "error": "AMBIGUOUS_SKILL_SLUG",
                      "matches": [
                        {
                          "ownerHandle": "legacyowner",
                          "slug": "skill-vetter",
                          "ref": "@legacyowner/skill-vetter"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://clawhub.ai/") };
        var provider = new ClawHubSkillExplorerProvider(http);

        var preview = await provider.PreviewSkillMarkdownAsync("skill-vetter");
        Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
        var ambiguous = Assert.IsType<DysonSkillExplorerPreviewOutcome.Ambiguous>(preview.Value);
        Assert.Single(ambiguous.Matches);
        Assert.Equal("legacyowner/skill-vetter", ambiguous.Matches[0].Slug);
        Assert.Equal("@legacyowner/skill-vetter", ambiguous.Matches[0].Label);
    }

    [Fact]
    public async Task ClawHub_download_follows_public_github_handoff()
    {
        var repoZip = BuildRepoZipWithSkill(
            rootFolder: "repo-main",
            skillFolderPath: "skills/git",
            skillMarkdown: "# Git handoff",
            decoyFolderPath: "skills/other",
            decoyMarkdown: "# other");

        var handler = new StubHandler(req =>
        {
            var uri = req.RequestUri!.AbsoluteUri;
            if (uri.Contains("/api/v1/download", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "sourceRef": "public-github",
                          "repo": "acme/skills",
                          "commit": "abc123",
                          "path": "skills/git",
                          "contentHash": "deadbeef",
                          "archiveUrl": "https://codeload.github.com/acme/skills/zip/abc123"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            Assert.Contains("codeload.github.com/acme/skills/zip/abc123", uri, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(repoZip)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip") },
                },
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://clawhub.ai/") };
        var provider = new ClawHubSkillExplorerProvider(http);

        var root = Path.Combine(Path.GetTempPath(), "dyson-clawhub-gh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var installed = await provider.DownloadAsync("acme/git", fs);
            Assert.True(installed.IsSuccess, installed.IsError ? installed.Error : null);
            Assert.IsType<DysonSkillExplorerDownloadOutcome.Installed>(installed.Value);

            var skillMd = fs.ReadAllText(".dyson/skills/acme-git/SKILL.md");
            Assert.True(skillMd.IsSuccess, skillMd.IsError ? skillMd.Error : null);
            Assert.Contains("Git handoff", skillMd.Value, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ClawHub_429_includes_retry_after_without_retrying()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("rate limited"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(34));
            return response;
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://clawhub.ai/") };
        var provider = new ClawHubSkillExplorerProvider(http);

        var page = await provider.SearchAsync("git", limit: 10, offset: 0);
        Assert.True(page.IsError);
        Assert.Contains("429", page.Error, StringComparison.Ordinal);
        Assert.Contains("Retry-After: 34", page.Error, StringComparison.Ordinal);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SkillsSh_empty_query_returns_empty_page_without_http()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://skills.sh/") };
        var provider = new SkillsShSkillExplorerProvider(http);

        var page = await provider.SearchAsync("  ", limit: 10, offset: 0);
        Assert.True(page.IsSuccess, page.IsError ? page.Error : null);
        Assert.Empty(page.Value.Skills);
        Assert.Equal(0, page.Value.Total);
        Assert.False(page.Value.HasMore);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SkillsSh_search_maps_installs_to_stars_and_composite_slug()
    {
        var handler = new StubHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/api/search", req.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("q=pdf", req.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("limit=10", req.RequestUri.Query, StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "skills": [
                        {
                          "id": "anthropics/skills/pdf",
                          "skillId": "pdf",
                          "name": "pdf",
                          "installs": 168001,
                          "source": "anthropics/skills"
                        }
                      ],
                      "count": 1
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://skills.sh/") };
        var provider = new SkillsShSkillExplorerProvider(http);

        var page = await provider.SearchAsync("pdf", limit: 10, offset: 0);
        Assert.True(page.IsSuccess, page.IsError ? page.Error : null);
        Assert.Single(page.Value.Skills);
        Assert.Equal("anthropics/skills/pdf", page.Value.Skills[0].Slug);
        Assert.Equal("anthropics", page.Value.Skills[0].Author);
        Assert.Equal(168001, page.Value.Skills[0].Stars);
        Assert.False(page.Value.Skills[0].Verified);
    }

    [Fact]
    public async Task SkillsSh_preview_and_download_filter_github_skill_folder()
    {
        var repoZip = BuildRepoZipWithSkill(
            rootFolder: "skills-main",
            skillFolderPath: "skills/pdf",
            skillMarkdown: "# PDF\n\nFrom skills.sh.",
            decoyFolderPath: "skills/xlsx",
            decoyMarkdown: "# XLSX");

        var handler = new StubHandler(req =>
        {
            var uri = req.RequestUri!.AbsoluteUri;
            if (uri.Contains("/api/search", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"skills":[{"id":"anthropics/skills/pdf","skillId":"pdf","name":"pdf","installs":1,"source":"anthropics/skills"}],"count":1}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            Assert.Contains("api.github.com/repos/anthropics/skills/zipball", uri, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(repoZip)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip") },
                },
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://skills.sh/") };
        var provider = new SkillsShSkillExplorerProvider(http);

        var preview = await provider.PreviewSkillMarkdownAsync("anthropics/skills/pdf");
        Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
        var previewMd = Assert.IsType<DysonSkillExplorerPreviewOutcome.Markdown>(preview.Value);
        Assert.Contains("From skills.sh", previewMd.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("XLSX", previewMd.Content, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "dyson-skillssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var installed = await provider.DownloadAsync("anthropics/skills/pdf", fs);
            Assert.True(installed.IsSuccess, installed.IsError ? installed.Error : null);
            var installedPath = Assert.IsType<DysonSkillExplorerDownloadOutcome.Installed>(installed.Value);
            Assert.Equal(".dyson/skills/anthropics-skills-pdf", installedPath.RelativePath.Replace('\\', '/'));

            var skillMd = fs.ReadAllText(".dyson/skills/anthropics-skills-pdf/SKILL.md");
            Assert.True(skillMd.IsSuccess, skillMd.IsError ? skillMd.Error : null);
            Assert.Contains("From skills.sh", skillMd.Value, StringComparison.Ordinal);

            var decoy = fs.FileExists(".dyson/skills/anthropics-skills-pdf/skills/xlsx/SKILL.md");
            Assert.True(decoy.IsSuccess);
            Assert.False(decoy.Value);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void PackageInstall_filters_named_skill_folder_from_repo_zip()
    {
        var repoZip = BuildRepoZipWithSkill(
            rootFolder: "repo-main",
            skillFolderPath: "skills/pdf",
            skillMarkdown: "# pdf",
            decoyFolderPath: "skills/other",
            decoyMarkdown: "# other");

        var filtered = DysonSkillPackageInstall.FilterZipToNamedSkillFolder(repoZip, "pdf");
        Assert.True(filtered.IsSuccess, filtered.IsError ? filtered.Error : null);

        var md = DysonSkillPackageInstall.ReadSkillMarkdownFromZip(filtered.Value);
        Assert.True(md.IsSuccess, md.IsError ? md.Error : null);
        Assert.Contains("pdf", md.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("other", md.Value, StringComparison.Ordinal);

        var missing = DysonSkillPackageInstall.FilterZipToNamedSkillFolder(repoZip, "nope");
        Assert.True(missing.IsError);
        Assert.Contains("nope", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageInstall_sanitizes_composite_slug_and_writes_markdown()
    {
        var composite = DysonSkillPackageInstall.SanitizeFolderSlug("owner/repo/my-skill");
        Assert.True(composite.IsSuccess, composite.IsError ? composite.Error : null);
        Assert.Equal("owner-repo-my-skill", composite.Value);

        var plain = DysonSkillPackageInstall.SanitizeFolderSlug("git-commit-helper");
        Assert.True(plain.IsSuccess, plain.IsError ? plain.Error : null);
        Assert.Equal("git-commit-helper", plain.Value);

        var bad = DysonSkillPackageInstall.SanitizeFolderSlug("../evil");
        Assert.True(bad.IsError);

        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var written = DysonSkillPackageInstall.WriteSkillMarkdown("# hub skill\n", "owner-repo-my-skill", fs);
            Assert.True(written.IsSuccess, written.IsError ? written.Error : null);
            Assert.Equal(".dyson/skills/owner-repo-my-skill", written.Value.Replace('\\', '/'));

            var text = fs.ReadAllText(".dyson/skills/owner-repo-my-skill/SKILL.md");
            Assert.True(text.IsSuccess, text.IsError ? text.Error : null);
            Assert.Contains("hub skill", text.Value, StringComparison.Ordinal);

            var preview = DysonSkillPackageInstall.ReadSkillMarkdownFromZip(
                BuildSkillZip("nested", "# from zip", null, null));
            Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
            Assert.Contains("from zip", preview.Value, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static byte[] BuildRepoZipWithSkill(
        string rootFolder,
        string skillFolderPath,
        string skillMarkdown,
        string decoyFolderPath,
        string decoyMarkdown)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var root = rootFolder.TrimEnd('/') + "/";
            WriteZipText(zip, root + skillFolderPath.Trim('/') + "/SKILL.md", skillMarkdown);
            WriteZipText(zip, root + decoyFolderPath.Trim('/') + "/SKILL.md", decoyMarkdown);
        }

        return ms.ToArray();
    }

    private static void WriteZipText(ZipArchive zip, string entryName, string contents)
    {
        var entry = zip.CreateEntry(entryName.Replace('\\', '/'));
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(contents);
    }

    private static byte[] BuildSkillZip(
        string rootFolder,
        string skillMarkdown,
        string? extraRelativePath,
        string? extraContents)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var skill = zip.CreateEntry(rootFolder.TrimEnd('/') + "/SKILL.md");
            using (var writer = new StreamWriter(skill.Open(), Encoding.UTF8))
                writer.Write(skillMarkdown);

            if (extraRelativePath is not null && extraContents is not null)
            {
                var extra = zip.CreateEntry(rootFolder.TrimEnd('/') + "/" + extraRelativePath.Replace('\\', '/'));
                using var writer = new StreamWriter(extra.Open(), Encoding.UTF8);
                writer.Write(extraContents);
            }
        }

        return ms.ToArray();
    }

    private static byte[] BuildZipWithEntry(string entryName, string contents)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(contents);
        }

        return ms.ToArray();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            Responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Responder(request));
    }
}
