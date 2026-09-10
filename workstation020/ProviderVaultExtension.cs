using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private readonly List<ProviderConnection> _providerConnections = new();
    private bool _providerExtensionReady;

    private string ProviderConfigPath => Path.Combine(VaultRoot, "providers.json");

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_providerExtensionReady) return;
        _providerExtensionReady = true;

        LoadProviderConnections();
        ApplyProviderPreset("OpenAI");
        CommandInputBox.PreviewKeyDown += EnhancedProvider_PreviewKeyDown;
        SendCommandButton.PreviewMouseLeftButtonDown += EnhancedProvider_PreviewMouseDown;
        AppendDiagnostic("AI Provider Vault online. Unlimited encrypted connection entries enabled.");
    }

    private void ProviderType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_providerExtensionReady) return;
        var type = (ProviderTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "OpenAI";
        ApplyProviderPreset(type);
    }

    private void ApplyProviderPreset(string type)
    {
        if (ProviderNameBox is null || ProviderEndpointBox is null || ProviderModelBox is null) return;
        switch (type)
        {
            case "OpenAI":
                ProviderNameBox.Text = "OpenAI Primary";
                ProviderEndpointBox.Text = "https://api.openai.com/v1/responses";
                ProviderModelBox.Text = "gpt-5.6-luna";
                break;
            case "OpenRouter":
                ProviderNameBox.Text = "OpenRouter Free";
                ProviderEndpointBox.Text = "https://openrouter.ai/api/v1/chat/completions";
                ProviderModelBox.Text = "openrouter/free";
                break;
            case "Groq":
                ProviderNameBox.Text = "Groq Primary";
                ProviderEndpointBox.Text = "https://api.groq.com/openai/v1/chat/completions";
                ProviderModelBox.Text = "openai/gpt-oss-20b";
                break;
            case "Gemini":
                ProviderNameBox.Text = "Gemini Primary";
                ProviderEndpointBox.Text = "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
                ProviderModelBox.Text = "gemini-2.5-flash";
                break;
            case "Anthropic":
                ProviderNameBox.Text = "Claude Primary";
                ProviderEndpointBox.Text = "https://api.anthropic.com/v1/messages";
                ProviderModelBox.Text = "claude-sonnet-4-5";
                break;
            case "Mistral":
                ProviderNameBox.Text = "Mistral Primary";
                ProviderEndpointBox.Text = "https://api.mistral.ai/v1/chat/completions";
                ProviderModelBox.Text = "mistral-small-latest";
                break;
            case "Cerebras":
                ProviderNameBox.Text = "Cerebras Primary";
                ProviderEndpointBox.Text = "https://api.cerebras.ai/v1/chat/completions";
                ProviderModelBox.Text = "llama3.1-8b";
                break;
            default:
                ProviderNameBox.Text = "Custom AI";
                ProviderEndpointBox.Text = "";
                ProviderModelBox.Text = "";
                break;
        }
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var type = (ProviderTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Custom OpenAI-Compatible";
        var name = ProviderNameBox.Text.Trim();
        var endpoint = ProviderEndpointBox.Text.Trim();
        var model = ProviderModelBox.Text.Trim();
        var key = ProviderKeyBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(key))
        {
            StatusText.Text = "Provider name, endpoint, model and key are required";
            AppendCommand("LUMI > AI connection not saved. Name, endpoint, model and API key are required.");
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        try
        {
            SaveProviderSecret(id, key);
            _providerConnections.Add(new ProviderConnection(id, type, name, endpoint, model, ProviderEnabledBox.IsChecked == true, DateTimeOffset.UtcNow));
            SaveProviderConnections();
            ProviderKeyBox.Clear();
            RefreshProviderList();
            StatusText.Text = $"AI connection saved: {name}";
            AppendCommand($"LUMI > Added AI connection '{name}' ({type}, {model}). Key encrypted to this Windows account.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "AI connection save failed";
            AppendDiagnostic("Provider save failed: " + ex.Message);
        }
    }

    private async void TestProvider_Click(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider();
        if (provider is null)
        {
            AppendCommand("LUMI > Select a saved AI connection to test.");
            return;
        }

        StatusText.Text = $"Testing {provider.Name}...";
        try
        {
            var answer = await CallProviderAsync(provider, "Reply with exactly: LUMI PROVIDER TEST PASS");
            AppendCommand($"LUMI [{provider.Name}] > {answer}");
            StatusText.Text = $"Provider test PASS: {provider.Name}";
        }
        catch (Exception ex)
        {
            AppendCommand($"LUMI > Provider test failed for {provider.Name}: {ex.Message}");
            AppendDiagnostic($"Provider test failed ({provider.Name}): {ex}");
            StatusText.Text = $"Provider test failed: {provider.Name}";
        }
    }

    private void RemoveProvider_Click(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider();
        if (provider is null)
        {
            AppendCommand("LUMI > Select a saved AI connection to remove.");
            return;
        }

        try
        {
            _providerConnections.RemoveAll(x => x.Id == provider.Id);
            var secretPath = ProviderSecretPath(provider.Id);
            if (File.Exists(secretPath)) File.Delete(secretPath);
            SaveProviderConnections();
            RefreshProviderList();
            AppendCommand($"LUMI > Removed AI connection '{provider.Name}'.");
            StatusText.Text = "AI connection removed";
        }
        catch (Exception ex)
        {
            AppendDiagnostic("Provider removal failed: " + ex.Message);
        }
    }

    private void LoadProviderConnections()
    {
        _providerConnections.Clear();
        try
        {
            if (File.Exists(ProviderConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<List<ProviderConnection>>(File.ReadAllText(ProviderConfigPath));
                if (loaded is not null) _providerConnections.AddRange(loaded);
            }

            MigrateLegacyProvider("groq", "Groq", "Groq Migrated", "https://api.groq.com/openai/v1/chat/completions", "openai/gpt-oss-20b");
            MigrateLegacyProvider("openrouter", "OpenRouter", "OpenRouter Migrated", "https://openrouter.ai/api/v1/chat/completions", "openrouter/free");
            SaveProviderConnections();
        }
        catch (Exception ex)
        {
            AppendDiagnostic("Provider vault load warning: " + ex.Message);
        }
        RefreshProviderList();
    }

    private void MigrateLegacyProvider(string legacyName, string type, string name, string endpoint, string model)
    {
        if (_providerConnections.Any(p => p.Type.Equals(type, StringComparison.OrdinalIgnoreCase))) return;
        var old = LoadSecret(legacyName);
        if (string.IsNullOrWhiteSpace(old)) return;
        var id = Guid.NewGuid().ToString("N");
        SaveProviderSecret(id, old);
        _providerConnections.Add(new ProviderConnection(id, type, name, endpoint, model, true, DateTimeOffset.UtcNow));
    }

    private void SaveProviderConnections()
    {
        Directory.CreateDirectory(VaultRoot);
        var json = JsonSerializer.Serialize(_providerConnections, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ProviderConfigPath, json);
    }

    private void RefreshProviderList()
    {
        if (ProviderConnectionsList is null) return;
        ProviderConnectionsList.Items.Clear();
        foreach (var provider in _providerConnections)
        {
            var state = provider.Enabled ? "ENABLED" : "DISABLED";
            ProviderConnectionsList.Items.Add(new ListBoxItem
            {
                Tag = provider.Id,
                Content = $"{provider.Name}   •   {provider.Type}   •   {provider.Model}   •   {state}"
            });
        }
        ProviderCountText.Text = $"{_providerConnections.Count} configured";
    }

    private ProviderConnection? SelectedProvider()
    {
        if (ProviderConnectionsList.SelectedItem is not ListBoxItem item || item.Tag is not string id) return null;
        return _providerConnections.FirstOrDefault(p => p.Id == id);
    }

    private string ProviderSecretPath(string id) => Path.Combine(VaultRoot, $"provider-{id}.key");

    private void SaveProviderSecret(string id, string secret)
    {
        var clear = Encoding.UTF8.GetBytes(secret.Trim());
        var entropy = Encoding.UTF8.GetBytes("LumiWorkstation.ProviderVault.v1");
        var encrypted = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(ProviderSecretPath(id), encrypted);
    }

    private string LoadProviderSecret(string id)
    {
        var path = ProviderSecretPath(id);
        if (!File.Exists(path)) throw new InvalidOperationException("Encrypted API key is missing for this connection.");
        var entropy = Encoding.UTF8.GetBytes("LumiWorkstation.ProviderVault.v1");
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clear);
    }

    private async void EnhancedProvider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (IsLocalWorkstationCommand(CommandInputBox.Text)) return;
        var provider = ChooseProviderForPrompt();
        if (provider is null) return;
        e.Handled = true;
        await ExecuteProviderPromptAsync(provider);
    }

    private async void EnhancedProvider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsLocalWorkstationCommand(CommandInputBox.Text)) return;
        var provider = ChooseProviderForPrompt();
        if (provider is null) return;
        e.Handled = true;
        await ExecuteProviderPromptAsync(provider);
    }

    private ProviderConnection? ChooseProviderForPrompt()
    {
        var selected = SelectedProvider();
        if (selected is { Enabled: true }) return selected;

        var preference = new[] { "Groq", "OpenRouter", "Cerebras", "Gemini", "Mistral", "Anthropic", "OpenAI", "Custom OpenAI-Compatible" };
        foreach (var type in preference)
        {
            var found = _providerConnections.FirstOrDefault(p => p.Enabled && p.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
        }
        return _providerConnections.FirstOrDefault(p => p.Enabled);
    }

    private static bool IsLocalWorkstationCommand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var cmd = raw.Trim().ToLowerInvariant();
        return cmd is "help" or "?" or "connect" or "refresh" or "status" or "build" or "deploy" or "workspace"
            || cmd.Contains("check phone") || cmd.Contains("connect phone") || cmd.Contains("phone status")
            || cmd.Contains("diagnostic") || cmd.Contains("logcat") || cmd.Contains("black box")
            || cmd.Contains("build only") || (cmd.Contains("build") && cmd.Contains("install"))
            || cmd.Contains("android studio") || cmd.Contains("check lumi") || cmd.Contains("lumi health");
    }

    private async Task ExecuteProviderPromptAsync(ProviderConnection provider)
    {
        var instruction = CommandInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(instruction)) return;
        CommandInputBox.Clear();
        AppendCommand($"YOU > {instruction}");
        SaveInstruction(instruction, $"ROUTED_{provider.Type.ToUpperInvariant()}");
        SendCommandButton.IsEnabled = false;
        StatusText.Text = $"Thinking via {provider.Name}...";
        try
        {
            var answer = await CallProviderAsync(provider, instruction);
            AppendCommand($"LUMI [{provider.Name}] > {answer}");
            StatusText.Text = $"Answered via {provider.Name}";
            SaveInstruction(instruction, $"ANSWERED_{provider.Type.ToUpperInvariant()}");
        }
        catch (Exception ex)
        {
            AppendCommand($"LUMI > {provider.Name} failed: {ex.Message}");
            AppendDiagnostic($"AI provider failure ({provider.Name}): {ex}");
            StatusText.Text = $"Provider failed: {provider.Name}";
        }
        finally
        {
            SendCommandButton.IsEnabled = true;
            CommandInputBox.Focus();
        }
    }

    private async Task<string> CallProviderAsync(ProviderConnection provider, string instruction)
    {
        var key = LoadProviderSecret(provider.Id);
        var type = provider.Type;

        if (type.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            return await CallOpenAIResponsesAsync(provider, key, instruction);
        if (type.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            return await CallGeminiAsync(provider, key, instruction);
        if (type.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            return await CallAnthropicAsync(provider, key, instruction);
        return await CallOpenAICompatibleAsync(provider, key, instruction);
    }

    private async Task<string> CallOpenAIResponsesAsync(ProviderConnection provider, string key, string instruction)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, provider.Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = provider.Model,
            instructions = "You are Lumi's engineering copilot inside Lumi Workstation. Be concise and concrete. Help move the installed Lumi phone toward Release 1.0. Never claim a local action occurred unless the workstation actually performed it.",
            input = instruction
        }), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {Short(body)}");
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)) continue;
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                        return text.GetString()!.Trim();
            }
        }
        return "OpenAI returned no text output.";
    }

    private async Task<string> CallOpenAICompatibleAsync(ProviderConnection provider, string key, string instruction)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, provider.Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (provider.Type.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            req.Headers.TryAddWithoutValidation("X-Title", "Lumi Workstation");
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = provider.Model,
            messages = new object[]
            {
                new { role = "system", content = "You are Lumi's engineering copilot. Be concise, factual and action-oriented." },
                new { role = "user", content = instruction }
            }
        }), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {Short(body)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim()
            ?? "Provider returned no text output.";
    }

    private async Task<string> CallGeminiAsync(ProviderConnection provider, string key, string instruction)
    {
        var endpoint = provider.Endpoint.Replace("{model}", Uri.EscapeDataString(provider.Model));
        endpoint += endpoint.Contains('?') ? "&key=" + Uri.EscapeDataString(key) : "?key=" + Uri.EscapeDataString(key);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = instruction } } } },
            systemInstruction = new { parts = new[] { new { text = "You are Lumi's engineering copilot. Be concise and concrete." } } }
        }), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {Short(body)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()?.Trim()
            ?? "Gemini returned no text output.";
    }

    private async Task<string> CallAnthropicAsync(ProviderConnection provider, string key, string instruction)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, provider.Endpoint);
        req.Headers.TryAddWithoutValidation("x-api-key", key);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = provider.Model,
            max_tokens = 1200,
            system = "You are Lumi's engineering copilot. Be concise and concrete.",
            messages = new[] { new { role = "user", content = instruction } }
        }), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {Short(body)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()?.Trim()
            ?? "Anthropic returned no text output.";
    }

    private sealed record ProviderConnection(
        string Id,
        string Type,
        string Name,
        string Endpoint,
        string Model,
        bool Enabled,
        DateTimeOffset CreatedAt);
}
