using System.CommandLine;
using System.Text.Json;
using AcuCli.Core;

namespace acu_cli;

internal static class Program
{
    private const string DefaultScope = "api offline_access";
    private const string DefaultRedirectUri = "http://localhost:8389/callback";

    // ------------------------------------------------------------ shared options

    private static readonly Option<string> UrlOption = new("--url")
    {
        Description = "Site URL, e.g. https://acumatica.contou.com",
    };

    private static readonly Option<string> ClientIdOption = new("--client-id")
    {
        Description = "OAuth client ID of an OpenID Connect application registered in Acumatica",
    };

    private static readonly Option<string> ClientSecretOption = new("--client-secret")
    {
        Description = "OAuth client secret; enables the non-interactive client-credentials grant",
    };

    private static readonly Option<string> ScopeOption = new("--scope")
    {
        Description = $"OAuth scopes to request (default: {DefaultScope})",
    };

    private static readonly Option<string> GrantOption = new("--grant")
    {
        Description = "OAuth grant: authorization-code (browser) or client-credentials (default: auto-detect)",
    };

    private static readonly Option<string> RedirectUriOption = new("--redirect-uri")
    {
        Description = $"Redirect URI registered for the client (default: {DefaultRedirectUri})",
    };

    private static readonly Option<bool> NoBrowserOption = new("--no-browser")
    {
        Description = "Do not open a browser; print the sign-in URL instead",
    };

    private static readonly Option<string> EndpointOption = new("--endpoint", "-E")
    {
        Description = "Contract endpoint name (default: Default; custom endpoints work too)",
    };

    private static readonly Option<string> EndpointVersionOption = new("--endpoint-version", "--version")
    {
        Description = "Endpoint version, e.g. 25.200.001 (default: newest version installed on the site)",
    };

    private static readonly Option<bool> InsecureOption = new("--insecure")
    {
        Description = "Ignore TLS certificate errors (self-signed certificates)",
    };

    private static readonly Option<string> FilterOption = new("--filter", "-f")
    {
        Description = "OData $filter expression, e.g. \"InventoryID eq 'AALEGO500'\"",
    };

    private static readonly Option<string> SelectOption = new("--select", "-s")
    {
        Description = "Comma-separated fields to return, e.g. InventoryID,Description",
    };

    private static readonly Option<string> ExpandOption = new("--expand", "-e")
    {
        Description = "Comma-separated related views to expand, e.g. SalesOrderDetails",
    };

    private static readonly Option<string> CustomFieldsOption = new("--custom")
    {
        Description = "Comma-separated custom (not-in-contract) fields to return, e.g. UsrField1,UsrField2",
    };

    private static readonly Option<int?> TopOption = new("--top")
    {
        Description = "Maximum records for listings (default 20; 0 returns all)",
    };

    private static readonly Option<int?> SkipOption = new("--skip")
    {
        Description = "Records to skip for listings",
    };

    private static readonly Option<string> FormatOption = new("--format")
    {
        Description = "Output format for entity data: yaml, json, or table (default yaml)",
        DefaultValueFactory = _ => "yaml",
    };

    private static readonly Option<string> FileOption = new("--file", "-i")
    {
        Description = "Read the JSON request body from a file instead of stdin",
    };

    private static readonly Option<bool> RefreshOption = new("--refresh")
    {
        Description = "Re-download the endpoint schema instead of using the cache",
    };

    private static readonly Option<bool> AgentOption = new("--agent")
    {
        Description = "Agent mode: listings must use --select to keep responses small",
    };

    static Program()
    {
        FormatOption.AcceptOnlyFromAmong("yaml", "json", "table");
        GrantOption.AcceptOnlyFromAmong("authorization-code", "client-credentials");
    }

    // ------------------------------------------------------------ entry point

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Output is redirected; keep the default encoding.
        }

        var root = new RootCommand(
            """
            Interact with Acumatica ERP through its contract-based REST API via OAuth 2.0
            (default and custom endpoints).

            Get started: register an OpenID Connect application in Acumatica
            (Integration > OpenID Connect Applications), then sign in with
            `acu login --url <site> --client-id <id>` (add --client-secret for the
            non-interactive client-credentials grant). Then use `acu get`, `acu put`,
            `acu patch`, `acu delete`, `acu invoke` and `acu raw`. Use `acu schema ...`
            to explore endpoints (schema commands need no credentials).

            Connection settings come from command options, environment variables (ACU_URL,
            ACU_CLIENT_ID, ACU_CLIENT_SECRET, ACU_ENDPOINT, ACU_VERSION, ACU_INSECURE), or
            the config files saved by `acu login`. Sessions are stored per site: sign in to
            several sites and switch between them with `acu use`.
            """);

        root.Add(CreateLoginCommand());
        root.Add(CreateLogoutCommand());
        root.Add(CreateWhoamiCommand());
        root.Add(CreateConfigCommand());
        root.Add(CreateUseCommand());
        root.Add(CreateGetCommand());
        root.Add(CreatePutCommand());
        root.Add(CreatePatchCommand());
        root.Add(CreateDeleteCommand());
        root.Add(CreateInvokeCommand());
        root.Add(CreateSchemaCommand());
        root.Add(CreateRawCommand());

        return await root.Parse(args).InvokeAsync();
    }

    // ------------------------------------------------------------ commands

    private static Command CreateLoginCommand()
    {
        var command = new Command("login", "Sign in with OAuth 2.0 and save the session and connection settings.");
        command.Add(UrlOption);
        command.Add(ClientIdOption);
        command.Add(ClientSecretOption);
        command.Add(ScopeOption);
        command.Add(GrantOption);
        command.Add(RedirectUriOption);
        command.Add(NoBrowserOption);
        command.Add(EndpointOption);
        command.Add(EndpointVersionOption);
        command.Add(InsecureOption);

        command.SetAction(async (pr, ct) => await ExecPlainAsync(async () =>
        {
            var options = ResolveOptions(pr);
            var siteUrl = options.Url;
            var insecure = options.IgnoreCertificateErrors;

            var clientId = PickOption(pr, ClientIdOption, "ACU_CLIENT_ID")
                ?? throw new CliException(
                    "A client ID is required (--client-id). Register an OpenID Connect application in Acumatica " +
                    "(Integration > OpenID Connect Applications).");
            var clientSecret = PickOption(pr, ClientSecretOption, "ACU_CLIENT_SECRET");
            var scope = pr.GetValue(ScopeOption) ?? DefaultScope;

            var discovery = await AcuOidcDiscovery.DiscoverAsync(siteUrl, insecure, ct);

            var grant = pr.GetValue(GrantOption)
                ?? (clientSecret is not null && discovery.SupportsGrant("client_credentials")
                    ? "client-credentials"
                    : discovery.SupportsGrant("authorization_code")
                        ? "authorization-code"
                        : null)
                ?? throw new CliException(
                    "The identity provider supports neither client_credentials nor authorization_code " +
                    $"(it offers: {string.Join(", ", discovery.GrantTypesSupported)}). " +
                    "Pass --client-secret to use client credentials.");

            if (grant == "client-credentials" && !discovery.SupportsGrant("client_credentials"))
                throw new CliException("The identity provider does not support the client-credentials grant.");
            if (grant == "authorization-code" && !discovery.SupportsGrant("authorization_code"))
                throw new CliException("The identity provider does not support the authorization-code grant.");

            AcuTokenSet tokens;
            using (var oidc = new AcuOidcClient(insecure))
            {
                if (grant == "client-credentials")
                {
                    if (clientSecret is null)
                        throw new CliException("The client-credentials grant requires --client-secret.");
                    tokens = await oidc.ClientCredentialsAsync(
                        discovery.TokenEndpoint, clientId, clientSecret, WithoutOfflineAccess(scope), ct);
                }
                else
                {
                    var redirectUri = pr.GetValue(RedirectUriOption) ?? DefaultRedirectUri;
                    var (verifier, challenge) = AuthorizationCodeFlow.CreatePkcePair();
                    var state = AuthorizationCodeFlow.CreateState();
                    var authorizeUrl = AuthorizationCodeFlow.BuildAuthorizeUrl(
                        discovery.AuthorizationEndpoint, clientId, redirectUri, scope, state, challenge);

                    Console.WriteLine($"Sign-in URL: {authorizeUrl}");
                    if (!pr.GetValue(NoBrowserOption))
                        AuthorizationCodeFlow.TryOpenBrowser(authorizeUrl);

                    var code = await AuthorizationCodeFlow.ReceiveCodeAsync(
                        new Uri(redirectUri), state, TimeSpan.FromMinutes(5), ct);
                    tokens = await oidc.ExchangeAuthorizationCodeAsync(
                        discovery.TokenEndpoint, clientId, clientSecret, redirectUri, code, verifier, ct);
                }
            }

            AcuTokenStore.SaveSession(new AcuAuthSession
            {
                Site = siteUrl,
                ClientId = clientId,
                ClientSecret = clientSecret,
                Grant = grant,
                Scope = tokens.Scope ?? scope,
                TokenType = tokens.TokenType,
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresAtUtc = tokens.ExpiresAtUtc,
                TokenEndpoint = discovery.TokenEndpoint,
                RevocationEndpoint = discovery.RevocationEndpoint,
            });

            // Resolve and validate the endpoint/version now so data commands are ready.
            using (var probe = new AcuClient(options))
            {
                var version = await ResolveVersionAsync(probe, options.EndpointVersion, ct);
                new AcuConfig
                {
                    Url = siteUrl,
                    Endpoint = options.EndpointName,
                    Version = version,
                    Insecure = insecure,
                }.Save();
            }

            Console.WriteLine($"Signed in to {siteUrl} as client {clientId} ({grant}; token expires {tokens.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local)");
            Console.WriteLine($"Connection saved to {AcuConfig.FilePath}, tokens to {AcuTokenStore.FilePath}");
            if (clientSecret is not null)
                Console.WriteLine($"Warning: the client secret is stored in plaintext at {AcuTokenStore.FilePath}");
        }));

        return command;
    }

    private static Command CreateLogoutCommand()
    {
        var command = new Command("logout", "Revoke the OAuth tokens and remove all saved settings.");
        command.SetAction(async _ => await ExecPlainAsync(async () =>
        {
            var options = ResolveOptionsForLogout(out var siteUrl);
            var auth = siteUrl is not null ? AcuTokenStore.FindForSite(siteUrl) : null;
            if (auth is not null && auth.ClientId is not null && !string.IsNullOrWhiteSpace(auth.RevocationEndpoint))
            {
                using var oidc = new AcuOidcClient(options?.IgnoreCertificateErrors ?? false);
                if (auth.AccessToken is not null)
                    await oidc.RevokeAsync(auth.RevocationEndpoint, auth.ClientId, auth.ClientSecret, auth.AccessToken, default);
                if (auth.RefreshToken is not null)
                    await oidc.RevokeAsync(auth.RevocationEndpoint, auth.ClientId, auth.ClientSecret, auth.RefreshToken, default);
            }

            var removedTokens = siteUrl is not null && AcuTokenStore.DeleteSession(siteUrl);
            var removedConfig = siteUrl is not null && AcuConfig.DeleteProfile(siteUrl);
            if (removedTokens || removedConfig)
            {
                var message = $"Signed out of {siteUrl}; removed its session and connection profile";
                if (AcuConfig.ActiveUrl() is { } next)
                    message += $" (active site is now {next})";
                else
                    message += " (no sites left)";
                Console.WriteLine(message);
            }
            else
            {
                Console.WriteLine("Nothing to remove: no saved session found.");
            }
        }));
        return command;
    }

    private static Command CreateWhoamiCommand()
    {
        var command = new Command("whoami", "Verify the OAuth session and the configured endpoint (connectivity check).");
        AddConnectionOptions(command);

        command.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var info = await client.GetSiteInfoAsync(token);
            var schema = await GetSchemaAsync(client, refresh: false, token);

            Console.WriteLine($"Connected to {client.Options.Url} (Acumatica build {info.AcumaticaBuildVersion})");
            var auth = AcuTokenStore.FindForSite(client.Options.Url);
            if (auth is not null)
            {
                Console.WriteLine(
                    $"OAuth client {auth.ClientId} (grant: {auth.Grant}, scope: {auth.Scope}); " +
                    $"access token expires {auth.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local");
            }
            Console.WriteLine(
                $"Endpoint: {client.Options.EndpointName}/{client.Options.EndpointVersion} " +
                $"({schema.Entities.Count} entities in schema)");
        }));

        return command;
    }

    private static Command CreateConfigCommand()
    {
        var command = new Command("config", "Show the effective connection settings (options > environment > config files).");
        AddConnectionOptions(command);

        command.SetAction(async pr => await ExecPlainAsync(() =>
        {
            var options = ResolveOptions(pr);
            var auth = AcuTokenStore.FindForSite(options.Url);

            Console.WriteLine($"Config file: {AcuConfig.FilePath}");
            Console.WriteLine($"URL:             {options.Url}");
            Console.WriteLine($"Endpoint:        {options.EndpointName}/{options.EndpointVersion ?? "(newest installed)"}");
            Console.WriteLine($"Ignore TLS errors: {options.IgnoreCertificateErrors}");
            Console.WriteLine($"Schema cache:    {SchemaCache.RootPath}");
            Console.WriteLine($"Auth file:       {AcuTokenStore.FilePath}");
            if (auth is null)
            {
                Console.WriteLine("OAuth session:   (not signed in to this site)");
            }
            else
            {
                Console.WriteLine($"OAuth session:   client {auth.ClientId}, grant {auth.Grant}, scope {auth.Scope}");
                Console.WriteLine($"                 secret stored: {(auth.ClientSecret is not null ? "yes" : "no")}; " +
                    $"access token expires {auth.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local");
            }
            Console.WriteLine($"Sessions stored: {AcuTokenStore.All().Count()} ({string.Join(", ", AcuTokenStore.All().Select(s => s.Site))})");
            return Task.CompletedTask;
        }));

        return command;
    }

    private static Command CreateUseCommand()
    {
        var site = new Argument<string?>("site")
        {
            Description = "Site URL (or a unique part of it, e.g. 'contou') to switch to; omit to list saved sites",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var command = new Command("use", "Switch the active site between saved logins (or list them).");
        command.Add(site);

        command.SetAction(async pr => await ExecPlainAsync(() =>
        {
            var profiles = AcuConfig.Profiles();
            var sessions = AcuTokenStore.All().Where(s => s.Site is not null).ToList();
            var known = profiles.Select(p => p.Url!)
                .Union(sessions.Select(s => s.Site!), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var input = pr.GetValue(site);
            if (string.IsNullOrWhiteSpace(input))
            {
                if (known.Count == 0)
                {
                    Console.WriteLine("No saved sites. Run `acu login --url <site-url> ...` first.");
                    return Task.CompletedTask;
                }
                var active = AcuConfig.ActiveUrl();
                foreach (var url in known)
                {
                    var marker = string.Equals(url, active, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
                    var profile = profiles.FirstOrDefault(p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase));
                    var session = sessions.FirstOrDefault(s => string.Equals(s.Site, url, StringComparison.OrdinalIgnoreCase));
                    var endpoint = profile is not null
                        ? $"{profile.Endpoint ?? "Default"}/{profile.Version ?? "(newest installed)"}"
                        : "(no profile)";
                    var state = session is not null
                        ? $"{session.Grant} (expires {session.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} local)"
                        : "no session";
                    Console.WriteLine($"{marker} {url,-45} {endpoint,-22} {state}");
                }
                Console.WriteLine();
                Console.WriteLine("Switch with `acu use <url-or-part>`. ACU_URL overrides the active site for one command.");
                return Task.CompletedTask;
            }

            var matches = MatchSites(input, known);
            if (matches.Count == 0)
                throw new CliException($"No saved site matches '{input}'. Known: {string.Join(", ", known)}.");
            if (matches.Count > 1)
                throw new CliException($"'{input}' matches several sites; be more specific: {string.Join(", ", matches)}.");

            var target = matches[0];
            var targetProfile = profiles.FirstOrDefault(p => string.Equals(p.Url, target, StringComparison.OrdinalIgnoreCase));
            if (targetProfile is null)
                throw new CliException(
                    $"There is an OAuth session for {target} but no connection profile; " +
                    $"run `acu login --url {target} ...` to save one.");

            AcuConfig.SetActive(target);
            var hasSession = sessions.Any(s => string.Equals(s.Site, target, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(
                $"Active site: {target} ({targetProfile.Endpoint ?? "Default"}/{targetProfile.Version ?? "(newest installed)"})"
                + (hasSession ? "" : " (warning: no OAuth session for this site; run `acu login`)"));
            return Task.CompletedTask;
        }));

        return command;
    }

    /// <summary>Exact (case-insensitive) URL matches first, then substring matches.</summary>
    private static List<string> MatchSites(string input, IReadOnlyList<string> known)
    {
        var key = input.Trim().TrimEnd('/');
        var exact = known.Where(u => string.Equals(u.TrimEnd('/'), key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
            return exact;
        return known.Where(u => u.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static Command CreateGetCommand()
    {
        var entity = new Argument<string>("entity")
        {
            Description = "Entity name, e.g. StockItem, Customer, SalesOrder",
        };
        var keys = new Argument<List<string>>("keys")
        {
            Description = "Key values identifying a single record (composite keys in order, e.g. ACME MAIN)",
        };

        var command = new Command("get", "List entities or fetch a single record by key.");
        command.Add(entity);
        command.Add(keys);
        command.Add(FilterOption);
        command.Add(SelectOption);
        command.Add(ExpandOption);
        command.Add(CustomFieldsOption);
        command.Add(TopOption);
        command.Add(SkipOption);
        command.Add(FormatOption);
        command.Add(AgentOption);
        AddConnectionOptions(command);

        command.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var entityName = pr.GetValue(entity);
            if (string.IsNullOrWhiteSpace(entityName))
                throw new CliException("An entity name is required, e.g. `acu get StockItem`.");

            var keyValues = NormalizeKeys(pr.GetValue(keys));
            var select = pr.GetValue(SelectOption);

            if (keyValues.Count == 0 && IsAgentMode(pr) && string.IsNullOrWhiteSpace(select))
            {
                var schema = await GetSchemaAsync(client, refresh: false, token);
                var entity = schema.FindEntity(entityName);
                var fieldHint = entity is not null && entity.Fields.Count > 0
                    ? string.Join(",", entity.Fields.Take(3).Select(f => f.Name))
                    : "InventoryID";
                throw new CliException(
                    $"Agent mode requires --select for listings ({entityName} has " +
                    $"{entity?.Fields.Count ?? 0} fields). E.g. --select {fieldHint}. " +
                    "See `acu schema show " + entityName + "` for all fields.");
            }

            var query = new AcuQuery
            {
                Filter = pr.GetValue(FilterOption),
                Select = select,
                Expand = pr.GetValue(ExpandOption),
                Custom = pr.GetValue(CustomFieldsOption),
                Top = pr.GetValue(TopOption),
                Skip = pr.GetValue(SkipOption),
            };

            if (keyValues.Count > 0)
            {
                if (query.Top is not null || query.Skip is not null)
                    throw new CliException("--top and --skip apply to listings and cannot be combined with record keys.");
                // $filter/$select/$expand/$custom are valid for key lookups too.
                query = query with { Top = null, Skip = null };
            }
            else if (query.Top is null)
            {
                query = query with { Top = 20 };
            }

            var response = await client.GetAsync(entityName, keyValues, query, token);
            PrintResponse(response, pr.GetValue(FormatOption) ?? "json");
        }));

        return command;
    }

    private static Command CreatePutCommand()
    {
        var entity = new Argument<string>("entity")
        {
            Description = "Entity name, e.g. StockItem",
        };

        var command = new Command("put", "Insert or update a record (upsert). Key fields go in the body (YAML/JSON file or stdin).");
        command.Add(entity);
        command.Add(FileOption);
        command.Add(SelectOption);
        AddConnectionOptions(command);

        command.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var entityName = pr.GetValue(entity);
            if (string.IsNullOrWhiteSpace(entityName))
                throw new CliException("An entity name is required, e.g. `acu put StockItem --file item.yaml`.");

            var body = await Input.ReadRequiredBodyAsync(pr.GetValue(FileOption));
            var response = await client.PutAsync(entityName, YamlFormat.YamlToAcuJson(body), pr.GetValue(SelectOption), token);
            PrintResponse(response, pr.GetValue(FormatOption) ?? "yaml");
        }));

        return command;
    }

    private static Command CreatePatchCommand()
    {
        var entity = new Argument<string>("entity")
        {
            Description = "Entity name, e.g. StockItem",
        };

        var command = new Command("patch", "Update only the fields in the body of an existing record (key fields included).");
        command.Add(entity);
        command.Add(FileOption);
        command.Add(SelectOption);
        AddConnectionOptions(command);

        command.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var entityName = pr.GetValue(entity);
            if (string.IsNullOrWhiteSpace(entityName))
                throw new CliException("An entity name is required, e.g. `acu patch StockItem --file changes.yaml`.");

            var body = await Input.ReadRequiredBodyAsync(pr.GetValue(FileOption));
            var response = await client.PatchAsync(entityName, YamlFormat.YamlToAcuJson(body), pr.GetValue(SelectOption), token);
            PrintResponse(response, pr.GetValue(FormatOption) ?? "yaml");
        }));

        return command;
    }

    private static Command CreateDeleteCommand()
    {
        var entity = new Argument<string>("entity")
        {
            Description = "Entity name, e.g. StockItem",
        };
        var keys = new Argument<List<string>>("keys")
        {
            Description = "Key values of the record to delete (required)",
        };

        var command = new Command("delete", "Delete a record identified by its key values.");
        command.Add(entity);
        command.Add(keys);
        AddConnectionOptions(command);

        command.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var entityName = pr.GetValue(entity);
            if (string.IsNullOrWhiteSpace(entityName))
                throw new CliException("An entity name is required, e.g. `acu delete StockItem AALEGO500`.");

            var keyValues = NormalizeKeys(pr.GetValue(keys));
            if (keyValues.Count == 0)
                throw new CliException("At least one key value is required, e.g. `acu delete StockItem AALEGO500`.");

            var response = await client.DeleteAsync(entityName, keyValues, token);
            PrintResponse(response, pr.GetValue(FormatOption) ?? "yaml");
        }));

        return command;
    }

    private static Command CreateInvokeCommand()
    {
        var entity = new Argument<string>("entity")
        {
            Description = "Entity name the action belongs to, e.g. SalesOrder",
        };
        var action = new Argument<string>("action")
        {
            Description = "Action name, e.g. CancelSalesOrder (see `acu schema actions`)",
        };

        var command = new Command("invoke", "Invoke an action on an entity from a JSON body (file or stdin, default {}).");
        command.Add(entity);
        command.Add(action);
        command.Add(FileOption);
        AddConnectionOptions(command);

        command.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var entityName = pr.GetValue(entity);
            var actionName = pr.GetValue(action);
            if (string.IsNullOrWhiteSpace(entityName) || string.IsNullOrWhiteSpace(actionName))
                throw new CliException("An entity and action name are required, e.g. `acu invoke SalesOrder CancelSalesOrder`.");

            var body = await Input.TryReadStdinOrFileAsync(pr.GetValue(FileOption));
            var response = await client.InvokeActionAsync(
                entityName, actionName,
                string.IsNullOrWhiteSpace(body) ? null : YamlFormat.YamlToAcuJson(body), token);
            PrintResponse(response, pr.GetValue(FormatOption) ?? "yaml");
        }));

        return command;
    }

    private static Command CreateSchemaCommand()
    {
        var command = new Command("schema", "Inspect endpoints and entity schemas, cached under ~/.acu-cli/schema. Needs no credentials.");

        var endpoints = new Command("endpoints", "List the endpoints and versions installed on the site (public index).");
        AddSchemaConnectionOptions(endpoints);
        endpoints.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var info = await client.GetSiteInfoAsync(token);
            Console.WriteLine($"Acumatica build: {info.AcumaticaBuildVersion}");
            foreach (var name in info.EndpointNames())
            {
                var versions = info.VersionsOf(name);
                var rendered = versions.Count > 1
                    ? $"{string.Join(", ", versions.Skip(1).Reverse())} and {versions[0]} (newest)"
                    : string.Join(", ", versions);
                Console.WriteLine($"{name}: {rendered}");
            }
        }, requireAuth: false, needsVersion: false));

        var sync = new Command("sync", "Download and cache the endpoint's swagger.json.");
        AddSchemaConnectionOptions(sync);
        sync.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var schema = await GetSchemaAsync(client, refresh: true, token);
            PrintSchemaSummary(client, schema);
        }, requireAuth: false));

        var list = new Command("list", "List the entities of the endpoint (uses the cache, syncs when missing).");
        list.Add(RefreshOption);
        AddSchemaConnectionOptions(list);
        list.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var schema = await GetSchemaAsync(client, pr.GetValue(RefreshOption), token);
            foreach (var entity in schema.Entities)
            {
                var screen = entity.Screen is not null ? $" ({entity.Screen.Trim('(', ')')})" : "";
                Console.WriteLine(
                    $"{entity.Name}{screen} — {entity.Fields.Count} {Plural(entity.Fields.Count, "field")}, " +
                    $"{entity.Actions.Count} {Plural(entity.Actions.Count, "action")}");
            }
        }, requireAuth: false));

        var entityArg = new Argument<string>("entity")
        {
            Description = "Entity name, e.g. StockItem",
        };
        var show = new Command("show", "Show the fields and actions of one entity (uses the cache, syncs when missing).");
        show.Add(entityArg);
        show.Add(RefreshOption);
        AddSchemaConnectionOptions(show);
        show.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var schema = await GetSchemaAsync(client, pr.GetValue(RefreshOption), token);
            var entity = schema.FindEntity(pr.GetValue(entityArg) ?? "")
                ?? throw new CliException(
                    $"Entity '{pr.GetValue(entityArg)}' not found in endpoint " +
                    $"{schema.Endpoint}/{schema.Version}. See `acu schema list`.");

            var screen = entity.Screen is not null ? $" ({entity.Screen.Trim('(', ')')})" : "";
            Console.WriteLine($"{entity.Name}{screen}");
            Console.WriteLine($"Operations: {string.Join(", ", entity.Operations)}");
            Console.WriteLine($"Actions: {entity.Actions.Count}");
            foreach (var action in entity.Actions)
                Console.WriteLine($"  {action.Name}");
            Console.WriteLine($"Fields: {entity.Fields.Count} (every entity also has id, rowNumber, note, custom, error, files)");
            foreach (var field in entity.Fields)
            {
                var type = field.IsArray ? $"{field.Type}[]" : field.Type;
                Console.WriteLine($"  {field.Name}: {type}");
            }
        }, requireAuth: false));

        var entityFilterArg = new Argument<string?>("entity")
        {
            Description = "Optional entity name; when omitted, all entities with actions are listed",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var actions = new Command("actions", "List the actions of one entity or of the whole endpoint.");
        actions.Add(entityFilterArg);
        actions.Add(RefreshOption);
        AddSchemaConnectionOptions(actions);
        actions.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var schema = await GetSchemaAsync(client, pr.GetValue(RefreshOption), token);
            var filter = pr.GetValue(entityFilterArg);

            var entities = filter is null
                ? schema.Entities.Where(e => e.Actions.Count > 0).ToList()
                : [schema.FindEntity(filter)
                    ?? throw new CliException($"Entity '{filter}' not found. See `acu schema list`.")];

            foreach (var entity in entities)
            {
                Console.WriteLine($"{entity.Name}: {entity.Actions.Count} {Plural(entity.Actions.Count, "action")}");
                foreach (var action in entity.Actions)
                    Console.WriteLine($"  {action.Name}");
            }
        }, requireAuth: false));

        command.Add(endpoints);
        command.Add(sync);
        command.Add(list);
        command.Add(show);
        command.Add(actions);
        return command;
    }

    private static Command CreateRawCommand()
    {
        var method = new Argument<string>("method")
        {
            Description = "HTTP method: GET, POST, PUT, DELETE, PATCH",
        };
        var path = new Argument<string>("path")
        {
            Description = "Path and query relative to the site root, e.g. \"entity/Default/25.200.001/StockItem?$top=1\" (quote it)",
        };

        var command = new Command("raw", "Send an arbitrary authenticated request against the site (escape hatch).");
        command.Add(method);
        command.Add(path);
        command.Add(FileOption);
        AddConnectionOptions(command);

        command.SetAction(async (pr, ct) => await ExecAsync(pr, ct, async (client, token) =>
        {
            var httpMethod = (pr.GetValue(method) ?? "GET").Trim().ToUpperInvariant();
            if (httpMethod is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH"))
                throw new CliException($"Unsupported HTTP method '{httpMethod}'.");

            var requestPath = pr.GetValue(path);
            if (string.IsNullOrWhiteSpace(requestPath))
                throw new CliException("A request path is required, e.g. `acu raw GET \"entity/Default/25.200.001/StockItem?$top=1\"`.");

            var body = await Input.TryReadStdinOrFileAsync(pr.GetValue(FileOption));
            var response = await client.SendAsync(
                httpMethod, requestPath,
                string.IsNullOrWhiteSpace(body) ? null : body,
                ensureSuccess: false, token);

            Console.WriteLine($"HTTP {response.StatusCode}");
            if (response.Json is { } json)
                Output.PrintJson(json);
            else if (!string.IsNullOrWhiteSpace(response.Raw))
                Console.WriteLine(response.Raw);
        }, needsVersion: false));

        return command;
    }

    // ------------------------------------------------------------ plumbing

    private static void AddConnectionOptions(Command command)
    {
        command.Add(UrlOption);
        command.Add(EndpointOption);
        command.Add(EndpointVersionOption);
        command.Add(InsecureOption);
    }

    /// <summary>Schemas need a URL and endpoint but no credentials.</summary>
    private static void AddSchemaConnectionOptions(Command command)
    {
        command.Add(UrlOption);
        command.Add(EndpointOption);
        command.Add(EndpointVersionOption);
        command.Add(InsecureOption);
    }

    private static IReadOnlyList<string> NormalizeKeys(List<string>? keys) =>
        keys?.Where(key => !string.IsNullOrWhiteSpace(key)).ToList() ?? [];

    private static string Plural(int count, string singular) => count == 1 ? singular : singular + "s";

    private static string? PickOption(ParseResult pr, Option<string> option, string envVar)
    {
        var value = pr.GetValue(option);
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        value = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Resolves the endpoint version against the public site index: validates an explicitly
    /// requested version, or picks the newest installed version of the endpoint.
    /// </summary>
    private static async Task<string> ResolveVersionAsync(
        AcuClient client, string? requestedVersion, CancellationToken ct)
    {
        var info = await client.GetSiteInfoAsync(ct);
        var versions = info.VersionsOf(client.Options.EndpointName);
        if (versions.Count == 0)
            throw new CliException(
                $"Endpoint '{client.Options.EndpointName}' is not installed on {client.Options.Url}. " +
                $"Available endpoints: {string.Join(", ", info.EndpointNames())}.");

        if (string.IsNullOrWhiteSpace(requestedVersion))
            return versions[0];

        var match = versions.FirstOrDefault(
            v => string.Equals(v, requestedVersion.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new CliException(
                $"Endpoint '{client.Options.EndpointName}' version {requestedVersion.Trim()} is not installed on " +
                $"{client.Options.Url}. Available versions: {string.Join(", ", versions)}.");
        return match;
    }

    /// <summary>
    /// Loads the endpoint schema from the cache (when fresh and fetched from the same site)
    /// or downloads and caches it. Works for the default endpoint and custom ones alike.
    /// </summary>
    private static async Task<AcuEndpointSchema> GetSchemaAsync(
        AcuClient client, bool refresh, CancellationToken ct)
    {
        var endpoint = client.Options.EndpointName;
        var version = client.Options.EndpointVersion;
        if (string.IsNullOrWhiteSpace(version))
            throw new AcuConfigurationException(
                "No endpoint version is configured. Pass --endpoint-version (e.g. 25.200.001), set ACU_VERSION, " +
                "or run `acu login`.");

        if (!refresh && SchemaCache.TryLoad(client.Options.Url, endpoint, version) is { } cached)
            return cached;

        var swaggerJson = await client.GetSwaggerAsync(ct);
        var schema = AcuEndpointSchema.Parse(swaggerJson, endpoint, version);
        SchemaCache.Save(client.Options.Url, endpoint, version, swaggerJson, schema);
        return schema;
    }

    private static void PrintSchemaSummary(AcuClient client, AcuEndpointSchema schema)
    {
        var actionCount = schema.Entities.Sum(e => e.Actions.Count);
        Console.WriteLine($"Synced {schema.Endpoint}/{schema.Version} from {client.Options.Url}");
        Console.WriteLine($"Entities: {schema.Entities.Count}, actions: {actionCount}");
        Console.WriteLine($"Cached at {SchemaCache.SwaggerPath(schema.Endpoint, schema.Version)}");
    }

    /// <summary>
    /// Runs a command against a client: resolves settings (auto-picking the endpoint version
    /// from the public site index when unset, and renewing the OAuth token when required),
    /// executes the body and converts known failures into friendly error messages.
    /// </summary>
    private static async Task<int> ExecAsync(
        ParseResult pr,
        CancellationToken ct,
        Func<AcuClient, CancellationToken, Task> body,
        bool requireAuth = true,
        bool needsVersion = true)
    {
        try
        {
            var options = ResolveOptions(pr);

            if (needsVersion)
            {
                using var probe = new AcuClient(options);
                options = options with
                {
                    EndpointVersion = await ResolveVersionAsync(probe, options.EndpointVersion, ct),
                };
            }

            if (requireAuth)
            {
                var auth = await AcuTokenStore.EnsureValidTokenAsync(
                    options.Url, options.IgnoreCertificateErrors, ct);
                options = options with { AccessToken = auth.AccessToken };
            }

            using var client = new AcuClient(options);
            await body(client, ct);
            return 0;
        }
        catch (CliException ex)
        {
            return Fail(ex.Message);
        }
        catch (AcuConfigurationException ex)
        {
            return Fail(ex.Message);
        }
        catch (AcuApiException ex)
        {
            return Fail(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Could not reach Acumatica: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return Fail($"The server returned something that is not valid JSON: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return Fail("The request timed out.");
        }
        catch (OperationCanceledException)
        {
            return Fail("Cancelled.");
        }
    }

    private static async Task<int> ExecPlainAsync(Func<Task> body)
    {
        try
        {
            await body();
            return 0;
        }
        catch (CliException ex)
        {
            return Fail(ex.Message);
        }
        catch (AcuConfigurationException ex)
        {
            return Fail(ex.Message);
        }
        catch (AcuApiException ex)
        {
            return Fail(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Could not reach Acumatica: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return Fail("The request timed out.");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    /// <summary>Resolves connection settings for logout, tolerating a missing config file.</summary>
    private static AcuClientOptions? ResolveOptionsForLogout(out string? siteUrl)
    {
        var config = AcuConfig.Load();
        siteUrl = Environment.GetEnvironmentVariable("ACU_URL") ?? config.Url;
        if (string.IsNullOrWhiteSpace(siteUrl))
            return null;
        return new AcuClientOptions
        {
            Url = siteUrl.Trim().TrimEnd('/'),
            IgnoreCertificateErrors = config.Insecure,
        };
    }

    private static AcuClientOptions ResolveOptions(ParseResult pr)
    {
        var config = AcuConfig.Load();

        string? Pick(Option<string> option, string envVar, string? fromConfig)
        {
            var value = pr.GetValue(option);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            return fromConfig;
        }

        var url = Pick(UrlOption, "ACU_URL", config.Url);

        // Endpoint/version/TLS settings belong to the site the profile was saved for —
        // logging into a different site starts fresh instead of inheriting them.
        var sameSite = url is not null && config.Url is not null
            && string.Equals(url.Trim().TrimEnd('/'), config.Url.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        var endpoint = Pick(EndpointOption, "ACU_ENDPOINT", sameSite ? config.Endpoint : null) ?? "Default";
        var version = Pick(EndpointVersionOption, "ACU_VERSION", sameSite ? config.Version : null);
        var insecure = pr.GetValue(InsecureOption)
            || IsTruthy(Environment.GetEnvironmentVariable("ACU_INSECURE"))
            || (sameSite && config.Insecure);

        if (string.IsNullOrWhiteSpace(url))
            throw new CliException(
                "No Acumatica URL configured. Run `acu login --url <site-url> ...`, set ACU_URL, or pass --url.");

        return new AcuClientOptions
        {
            Url = url!.Trim().TrimEnd('/'),
            EndpointName = endpoint.Trim(),
            EndpointVersion = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            IgnoreCertificateErrors = insecure,
        };
    }

    private static bool IsTruthy(string? value) =>
        value is { Length: > 0 }
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string WithoutOfflineAccess(string scope) =>
        string.Join(" ", scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.Equals(part, "offline_access", StringComparison.OrdinalIgnoreCase)));

    private static bool IsAgentMode(ParseResult pr) =>
        pr.GetValue(AgentOption) || IsTruthy(Environment.GetEnvironmentVariable("ACU_AGENT"));

    private static void PrintResponse(AcuResponse response, string format)
    {
        if (response.Json is { } json)
        {
            if (string.Equals(format, "table", StringComparison.OrdinalIgnoreCase))
                Output.PrintTable(json);
            else if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                Output.PrintJson(json);
            else
                YamlFormat.PrintYaml(json);
        }
        else if (!string.IsNullOrWhiteSpace(response.Raw))
        {
            Console.WriteLine(response.Raw);
        }
        else
        {
            Console.WriteLine($"OK ({response.StatusCode})");
        }
    }
}
