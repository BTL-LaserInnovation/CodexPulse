using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow
{
    private string? renderedCreditData;
    private static string FriendlyPlan(string? plan) => plan switch
    {
        null or "" => "플랜 미제공",
        "go" => "Go",
        "team" => "Business",
        _ when plan.Contains("business", StringComparison.OrdinalIgnoreCase) => "Business",
        _ when plan.Contains("enterprise", StringComparison.OrdinalIgnoreCase) => "Enterprise",
        _ when plan.Contains("edu", StringComparison.OrdinalIgnoreCase) => "Edu",
        _ when plan.Contains("pro", StringComparison.OrdinalIgnoreCase) => "Pro",
        _ when plan.Contains("plus", StringComparison.OrdinalIgnoreCase) => "Plus",
        _ when plan.Contains("free", StringComparison.OrdinalIgnoreCase) => "Free",
        _ => "ChatGPT"
    };

    private void RenderAccount()
    {
        AccountText.Text = accountEmail is { Length: > 0 } email ? (connected ? email : "이전 계정 · " + email) : needsLogin ? "ChatGPT 계정 로그인 필요" : "연동 계정 확인 중…";
        AccountText.ToolTip = AccountText.Text;
        PlanText.Text = FriendlyPlan(accountPlan);
        ConnectButton.IsEnabled = !creditAction && !refreshing && !updateApplying;
    }

    private void RenderCredits()
    {
        if (!ready) return;
        CreditsTitle.Text = creditBalance.AvailableCount is { } count ? $"리셋권 {count}개 · 만료순" : "리셋권 · 보유 수 미확인";
        CreditsTitle.ToolTip = creditBalance.AvailableCount is { } available && available > creditBalance.Credits.Count
            ? $"보유 {available}개 중 서버가 {creditBalance.Credits.Count}개의 상세 정보만 제공했습니다."
            : "만료가 빠른 순서입니다. 만료 시각 미제공 항목은 맨 아래에 표시합니다.";
        var data = JsonSerializer.Serialize(creditBalance);
        if (renderedCreditData == data)
        {
            foreach (var existing in CreditList.Children.OfType<Grid>()) UpdateCreditRow(existing);
            return;
        }
        renderedCreditData = data;
        CreditList.Children.Clear();
        CreditsHint.Visibility = Visibility.Collapsed;
        if (creditBalance.Credits.Count == 0)
        {
            CreditsHint.Visibility = Visibility.Visible;
            CreditsHint.Text = creditBalance.AvailableCount == 0 ? "보유한 리셋권이 없습니다." : creditBalance.DetailsKnown ? "표시할 리셋권 상세 정보가 없습니다." : "서버가 리셋권 상세 정보를 제공하지 않았습니다.";
            return;
        }
        foreach (var credit in creditBalance.Credits.OrderBy(c => c.ExpiresAt is null).ThenBy(c => c.ExpiresAt).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            var row = new Grid { Height = 22, Margin = new Thickness(0, 0, 0, 2), Tag = credit, HorizontalAlignment = HorizontalAlignment.Left };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            var name = new TextBlock
            {
                Text = credit.DisplayName.ReplaceLineEndings(" "), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0), ToolTip = credit.DisplayName
            };
            System.Windows.Automation.AutomationProperties.SetName(name, "리셋권 이름: " + credit.DisplayName);
            row.Children.Add(name);
            var deadline = new TextBlock { FontSize = ListFontSize, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            System.Windows.Automation.AutomationProperties.SetName(deadline, "리셋권 만료 시각");
            Grid.SetColumn(deadline, 1);
            row.Children.Add(deadline);
            var remaining = new TextBlock { FontSize = ListFontSize, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            System.Windows.Automation.AutomationProperties.SetName(remaining, "리셋권 남은 시간");
            Grid.SetColumn(remaining, 2);
            row.Children.Add(remaining);
            var use = new Button
            {
                Content = "사용", Tag = credit.Id, Height = 20, Width = 32, FontSize = ListFontSize, Padding = new Thickness(3, 0, 3, 0),
                Foreground = Theme("MintBrush"), IsEnabled = connected && !creditAction && !refreshing && !updateApplying && creditBalance.AvailableCount != 0 && credit.IsAvailable(DateTimeOffset.UtcNow)
            };
            use.Click += UseResetCredit_Click;
            System.Windows.Automation.AutomationProperties.SetName(use, credit.DisplayName + " 사용 확인");
            Grid.SetColumn(use, 3);
            row.Children.Add(use);
            UpdateCreditRow(row);
            CreditList.Children.Add(row);
        }
    }

    private void UpdateCreditRow(Grid row)
    {
        if (row.Tag is not ResetCredit credit) return;
        var deadline = (TextBlock)row.Children[1];
        var remaining = (TextBlock)row.Children[2];
        deadline.Text = credit.ExpiresAt is { } expires ? $"{LocalTime.Convert(expires):MM/dd HH:mm}" : "기한 미제공";
        remaining.Text = credit.ExpiresAt is { } until ? until <= DateTimeOffset.UtcNow ? "만료됨" : Countdown(until) : "—";
        deadline.Foreground = Theme(credit.ExpiresAt is { } end && end - DateTimeOffset.UtcNow < TimeSpan.FromDays(2) ? "CoralBrush" : "MutedBrush");
        remaining.Foreground = deadline.Foreground;
        ((Button)row.Children[3]).IsEnabled = connected && !creditAction && !refreshing && !updateApplying && creditBalance.AvailableCount != 0 && credit.IsAvailable(DateTimeOffset.UtcNow);
        row.ToolTip = $"{credit.DisplayName}\n{credit.Description}\n{(credit.ExpiresAt is { } time ? $"만료: {LocalTime.Convert(time):yyyy-MM-dd HH:mm:ss} {LocalTime.Label(time)}" : "서버가 만료 시각을 제공하지 않았습니다.")}";
    }

    private static string Countdown(DateTimeOffset until)
    {
        var remaining = until - DateTimeOffset.UtcNow;
        return remaining <= TimeSpan.Zero ? "확인 대기" : remaining.TotalDays >= 1 ? $"{(int)remaining.TotalDays}일 {remaining.Hours}시간 남음" :
            remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}시간 {remaining.Minutes}분 남음" : $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}분 남음";
    }

    private static string RateIdentity(string fingerprint, JsonElement result) =>
        result.TryGetProperty("accountId", out var accountId) && accountId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(accountId.GetString())
            ? HashIdentity(fingerprint + ":" + accountId.GetString()) : fingerprint;

    private async void UseResetCredit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string creditId } || creditAction || refreshing || closing || updateApplying || !connected) return;
        creditAction = true;
        RenderCredits();
        FooterRefreshButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        var transmitted = false;
        string? finalMessage = null;
        var finalSuccess = false;
        string? operationAccount = null;
        try
        {
            await RefreshAsync(force: true);
            if (!connected || client is not { } current || state.ActiveAccount is not { } identity || accountFingerprint is not { } fingerprint)
                throw new InvalidOperationException("현재 계정과 연결을 먼저 확인하세요.");
            var selected = creditBalance.Credits.Find(c => c.Id == creditId);
            if (selected is null || creditBalance.AvailableCount == 0 || !selected.IsAvailable(DateTimeOffset.UtcNow))
                throw new InvalidOperationException("선택한 리셋권을 더 이상 사용할 수 없습니다.");
            if (state.PendingResetUses.TryGetValue(identity, out var previous) && previous.CreditId != creditId && creditBalance.Credits.Any(c => c.Id == previous.CreditId))
                throw new InvalidOperationException("이전 리셋권 요청의 결과가 미확인입니다. 같은 권을 선택해 확인하세요.");
            var revision = current.AccountRevision;
            var generation = Interlocked.Read(ref accountGeneration);
            resetDialog = new(accountEmail ?? "이메일 미제공", FriendlyPlan(accountPlan), selected)
            {
                Owner = this, Topmost = Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var confirmed = resetDialog.ShowDialog() == true;
            resetDialog = null;
            if (!confirmed || closing) return;

            var before = await current.ReadAccountAsync(lifetime.Token);
            var limits = await current.ReadRateLimitsAsync(lifetime.Token);
            var after = await current.ReadAccountAsync(lifetime.Token);
            if (current.AccountRevision != revision || Interlocked.Read(ref accountGeneration) != generation ||
                !before.TryGetProperty("account", out var beforeAccount) || beforeAccount.ValueKind != JsonValueKind.Object ||
                !after.TryGetProperty("account", out var afterAccount) || afterAccount.ValueKind != JsonValueKind.Object ||
                AccountKey(before, beforeAccount) != fingerprint || AccountKey(after, afterAccount) != fingerprint ||
                RateIdentity(fingerprint, limits) != identity)
                throw new InvalidOperationException("확인 중 계정이 변경되어 사용하지 않았습니다. 다시 선택하세요.");
            var fresh = ResetCreditBalance.Parse(limits);
            var verifiedCredit = fresh.Credits.Find(c => c.Id == creditId);
            if (fresh.AvailableCount == 0 || verifiedCredit is null || !verifiedCredit.IsAvailable(DateTimeOffset.UtcNow) ||
                verifiedCredit.ResetType != selected.ResetType || verifiedCredit.ExpiresAt != selected.ExpiresAt)
                throw new InvalidOperationException("리셋권 정보 또는 사용 가능 상태가 변경되었습니다. 다시 선택하세요.");
            var operation = state.PendingResetUses.TryGetValue(identity, out var pending) && pending.CreditId == creditId
                ? pending : new ResetUseOperation(creditId, Guid.NewGuid().ToString("D"));
            state.PendingResetUses[identity] = operation;
            operationAccount = identity;
            if (!store.Save(state)) throw new InvalidOperationException("중복 사용 방지 기록을 저장하지 못해 요청을 보내지 않았습니다.");
            if (current.AccountRevision != revision || Interlocked.Read(ref accountGeneration) != generation)
                throw new InvalidOperationException("계정이 변경되어 사용하지 않았습니다.");
            transmitted = true;
            var result = await current.ConsumeResetCreditAsync(operation.IdempotencyKey, creditId, lifetime.Token);
            var outcome = result.TryGetProperty("outcome", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (outcome is "reset" or "alreadyRedeemed" or "nothingToReset" or "noCredit")
            {
                state.PendingResetUses.Remove(identity);
                store.Save(state);
            }
            finalSuccess = outcome is "reset" or "alreadyRedeemed";
            finalMessage = outcome switch
            {
                "reset" => "리셋권을 사용했습니다",
                "alreadyRedeemed" => "이미 처리된 리셋 요청을 확인했습니다",
                "nothingToReset" => "초기화할 한도가 없어 사용되지 않았습니다",
                "noCredit" => "사용 가능한 리셋권이 없습니다",
                _ => "처리 결과 미확인 · 다시 조회하세요"
            };
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            finalMessage = transmitted ? "사용 결과 미확인 · 같은 권으로 재확인하세요" : ex.Message;
        }
        finally
        {
            resetDialog = null;
            if (!closing)
            {
                await RefreshAsync(force: true);
                if (transmitted && operationAccount != state.ActiveAccount)
                {
                    finalMessage = "이전 계정의 리셋 요청 후 계정이 변경되었습니다";
                    finalSuccess = false;
                }
                if (finalMessage is not null) SetStatus(finalMessage, finalSuccess);
            }
            creditAction = false;
            if (!closing)
            {
                FooterRefreshButton.IsEnabled = !refreshing && !updateApplying;
                RenderAccount();
                RenderCredits();
                if (refreshQueued)
                {
                    refreshQueued = false;
                    _ = RefreshAsync();
                }
            }
        }
    }
}
