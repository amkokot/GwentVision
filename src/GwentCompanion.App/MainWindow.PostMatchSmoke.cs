using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    /// <summary>Hidden/offline UI and stop-order smoke; no game capture or live-cache writes.</summary>
    internal static async Task<int> RunPostMatchSmokeAsync()
    {
        var folder=Path.Combine(FindGameRoot(),"GwentCompanion/diagnostics/post-match-ui-smoke"); Directory.CreateDirectory(folder);
        OpponentReviewWindow? editor=null; MainWindow? window=null;
        void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); }
        object? Call(string method,params object?[] args)=>typeof(DeckBuilderWindow).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(editor,args);
        DeckAutoFillResult Result()=>(DeckAutoFillResult)typeof(DeckBuilderWindow).GetField("_result",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor)!;
        async Task Settled()
        {
            var until=DateTimeOffset.UtcNow.AddSeconds(20);
            while(editor!.BuilderStatus.Text.StartsWith("Updating",StringComparison.Ordinal))
            { if(DateTimeOffset.UtcNow>until) throw new TimeoutException("Builder did not settle."); await Task.Delay(40); }
            Check(!editor.BuilderStatus.Text.StartsWith("Could not",StringComparison.Ordinal),editor.BuilderStatus.Text);
        }
        Dictionary<string,string> Hashes()=>new[]{LibraryPath,OpponentMemoryPath,ObservedDraftPath,ResolveSettingsPath(),
            Path.Combine(FindGameRoot(),"GwentCompanion/sessions/20260831-122341/vision-observations.jsonl")}.Where(File.Exists)
            .ToDictionary(path=>path,path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        try
        {
            var before=Hashes(); var catalog=GwentOneCardCatalog.Load(CardDataPath); var library=DeckLibrary.Load(LibraryPath);
            var alba=catalog.Single(c=>c.Name=="Alba Armored Cavalry"); var knight=catalog.Single(c=>c.Name=="Nilfgaardian Knight");
            var at=DateTimeOffset.UtcNow; var cards=new[]{new ObservedCard(alba,CardProvenance.ProbableStartingDeck,1,at,"raw fixture")};
            var encounter=new LearnedOpponentEncounter("postmatch-ui",at,"Nilfgaard","Tactical Decision",16,null,25,25,cards,[],OpponentProvisionCalculator.Calculate(cards,166));
            var memory=new OpponentDeckMemoryStore(); var record=memory.Capture(encounter,"Opponent review fixture",null,null,"fixture");
            record=memory.Suggest(record.Id,[new(alba,2),new(knight)]);
            var raw=JsonSerializer.Serialize(record.Encounters); DeckEditorDraft? savedDraft=null;
            ImageSource? Art(CardDefinition card)
            {
                var path=Path.Combine(FindGameRoot(),"GwentCompanion/cache/portraits",card.Id+".jpg"); if(!File.Exists(path)) return null;
                var image=new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.UriSource=new Uri(path); image.EndInit(); image.Freeze(); return image;
            }
            var openTimer=System.Diagnostics.Stopwatch.StartNew();
            editor=new OpponentReviewWindow(record,catalog,library,Path.Combine(folder,"library.json"),()=>{},(draft,later)=>
            {
                savedDraft=draft;
                memory.Review(record.Id,draft.Name,OpponentReviewDraft.Cards(record,draft),later,OpponentReviewDraft.Header(draft,catalog));
                memory.Save(Path.Combine(folder,"memory.json"));
            },Art);
            await Settled();
            openTimer.Stop();
            Check(openTimer.Elapsed<TimeSpan.FromSeconds(2),"Full-library opponent review opened too slowly: "+openTimer.Elapsed);
            File.WriteAllText(Path.Combine(folder,"opponent-review-timing.txt"),$"Full 2,651-list opponent review construction and first settled proposal: {openTimer.ElapsedMilliseconds}ms.");
            Check(editor.WindowState==WindowState.Maximized&&!editor.Topmost&&editor.IsReviewingOpponent(record.Id),"Review did not use the maximized shared builder.");
            Check(editor.AutoFill.IsChecked==true&&Result().Cards.Sum(c=>c.Count)==3,"Opening review did not present exactly the reviewed proposal as replaceable AUTO copies.");
            Check(editor.SaveObservedDraftButton.IsEnabled&&!editor.SaveDeckButton.IsEnabled&&editor.ReviewLaterChoice.IsChecked==true,"Incomplete review cannot be saved separately.");
            var rows=((IEnumerable)editor.DraftCards.Rows!).Cast<object>().ToArray();
            var details=rows.Select(row=>(string)row.GetType().GetProperty("Detail")!.GetValue(row)!).ToArray();
            var badges=rows.Select(row=>(string)row.GetType().GetProperty("Badge")!.GetValue(row)!).ToArray();
            Check(details.Count(text=>text.StartsWith("OBSERVED COPY"))==1&&details.Count(text=>text.StartsWith("PROPOSED · AUTO"))==2&&
                badges.Count(text=>text=="FIXED")==1&&badges.Count(text=>text=="AUTO")==2,
                "Suggested duplicate was labelled as observed.");
            object KnightRow()=>((IEnumerable)editor.DraftCards.Rows!).Cast<object>().Single(row=>
                row.GetType().GetProperty("Card")?.GetValue(row) is CardDefinition card&&card.Id==knight.Id);
            Call("ActivateDraftCard",KnightRow(),System.Windows.Input.ModifierKeys.None); await Settled();
            Check(Result().Cards.Sum(c=>c.Count)==3&&(string)KnightRow().GetType().GetProperty("Badge")!.GetValue(KnightRow())! == "FIXED",
                "First proposed-card click did not fix it in place.");
            Call("ActivateDraftCard",KnightRow(),System.Windows.Input.ModifierKeys.None); await Settled();
            Check(Result().Cards.Sum(c=>c.Count)==2&&Result().Cards.All(c=>c.Card.Id!=knight.Id),"Second proposed-card click did not remove it.");
            Check(!editor.OpenDraft(library.Decks.First(),()=>false),"Unsaved opponent edits were discarded.");
            Call("UndoEditClicked",editor,new RoutedEventArgs()); await Settled(); Check(Result().Cards.Sum(c=>c.Count)==3&&(string)KnightRow().GetType().GetProperty("Badge")!.GetValue(KnightRow())! == "FIXED","Undo did not restore the fixed proposal.");
            Call("UndoEditClicked",editor,new RoutedEventArgs()); await Settled(); Check(Result().Cards.Sum(c=>c.Count)==3&&(string)KnightRow().GetType().GetProperty("Badge")!.GetValue(KnightRow())! == "AUTO","Undo did not restore AUTO proposal state.");
            editor.AutoFill.IsChecked=false; await Settled(); Check(Result().Cards.Sum(c=>c.Count)==1,"Turning off auto-fill did not remove every proposed copy together.");
            Check(editor.SaveObservedDraftButton.IsEnabled&&!editor.SaveDeckButton.IsEnabled,"Incomplete opponent list did not retain its separate save path.");
            Call("SaveObservedDraftLocal");
            var partial=OpponentDeckMemoryStore.Load(Path.Combine(folder,"memory.json")).Records.Single();
            Check(partial.Cards.Sum(c=>c.ObservedCopies)==1&&partial.Complete==false&&savedDraft?.Cards.Sum(c=>c.Count)==1,
                "Incomplete/provision-invalid review save was gated by playable-deck validation.");
            Call("UndoEditClicked",editor,new RoutedEventArgs()); await Settled(); Check(Result().Cards.Sum(c=>c.Count)==3&&editor.AutoFill.IsChecked==true,"Undo did not restore all proposals.");
            editor.DeckName.Text="Reviewed opponent fixture";
            var leader=GwentOneCardCatalog.StartingLeaders(catalog).First(c=>c.Faction=="Nilfgaard"&&c.Name!="Tactical Decision");
            editor.LeaderChoice.SelectedItem=editor.LeaderChoice.Items.Cast<object>().Single(item=>
                (item.GetType().GetProperty("Card")!.GetValue(item) as CardDefinition)?.Id==leader.Id);
            await Settled(); Call("SaveObservedDraftLocal");
            var saved=OpponentDeckMemoryStore.Load(Path.Combine(folder,"memory.json")).Records.Single();
            Check(saved.Name==editor.DeckName.Text&&saved.Leader==leader.Name&&saved.Cards.Sum(c=>c.ObservedCopies)==3&&savedDraft?.SourceKey==OpponentReviewDraft.SourceKey(record),"Review save lost name/cards/header/source.");
            Check(JsonSerializer.Serialize(saved.Encounters)==raw&&saved.EncounterCount==1&&!saved.Complete,"Review rewrote encounter evidence.");
            DeckBuilderSmoke.Render(editor,Path.Combine(folder,"opponent-review-wide.png"),1320,900);
            DeckBuilderSmoke.Render(editor,Path.Combine(folder,"opponent-review-compact.png"),780,600);
            Check(editor.Collection.ActualHeight>=70&&editor.DraftCards.ActualHeight>=70,"Compact review squeezed card lists out.");
            editor.OpenDraft(library.Decks.First()); await Settled();
            Check(editor.ReviewLaterChoice.Visibility==Visibility.Collapsed&&editor.EditorTitle.Text=="DECK EDITOR", "Review actions leaked into ordinary builder mode.");

            window=new MainWindow(); window.Loaded-=window.OnLoaded;
            window._candidateCatalog=catalog; window._library=library; window._cachedDecks=library.Decks;
            Check(window.AutoStopOnMmrChoice.IsChecked==false,"Auto-stop default changed existing behavior.");
            var setting=JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(new UserSettings(null,AutoStopOnMmr:true)));
            Check(setting?.AutoStopOnMmr==true&&!JsonSerializer.Deserialize<UserSettings>("{}")!.AutoStopOnMmr,"New/legacy preference serialization failed.");
            window._libraryReady=false; // Changing UI preferences in this smoke must never persist live settings.
            window.AutoStopOnMmrChoice.IsChecked=true;
            window.HideLoadingShell(); window.ShowPage(UiPage.Settings);
            DeckBuilderSmoke.Render(window,Path.Combine(folder,"mmr-setting.png"),510,850);
            var mmrResult=new CardVisionResult(at,new(GwentViewKind.Board,false,0,0,null,ScreenHeader:"DEFEAT",
                PostMatchMmr:new(2378,null,true,"fixture",2440)),[],[],false);
            var drained=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var savedEncounter=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window._visionWorker=drained.Task; window._autoEncounterSave=savedEncounter.Task; window._builderWindow=editor;
            window.RefreshAnalysisButton(); Check(window.DiagnosticButton.IsEnabled,"Open post-match builder disabled manual Stop.");
            var reviewField=typeof(MainWindow).GetField("_reviewEvidencePath",BindingFlags.NonPublic|BindingFlags.Instance)!;
            reviewField.SetValue(window,folder); window.TryAutoStopOnMmr(mmrResult);
            Check(window._autoStopTask is null,"Offline review triggered automatic stop."); reviewField.SetValue(window,null);
            window._analysisTransition=true; window.TryAutoStopOnMmr(mmrResult); Check(window._autoStopTask is null,"Concurrent transition triggered another stop.");
            window._analysisTransition=false; window.TryAutoStopOnMmr(mmrResult);
            var stopping=window._autoStopTask!;
            Check(stopping is not null&&!stopping.IsCompleted&&window._analysisTransition,"Auto-stop did not wait for retained analysis.");
            window.TryAutoStopOnMmr(mmrResult); Check(ReferenceEquals(stopping,window._autoStopTask),"Repeated MMR launched another stop.");
            drained.SetResult(); await Task.Delay(50);
            Check(!stopping!.IsCompleted,"Stop finished before encounter save.");
            savedEncounter.SetResult(); await stopping;
            Check(window._visionWorker is null&&!window._analysisTransition&&window.AnalysisStatusText.Text.Contains("automatically"),"Auto-stop did not finish/release controls.");
            window.TryAutoStopOnMmr(mmrResult); Check(ReferenceEquals(stopping,window._autoStopTask),"Late MMR restarted idle analysis.");
            window._builderWindow=null;
            var after=Hashes(); Check(before.Count==after.Count&&before.All(item=>after.GetValueOrDefault(item.Key)==item.Value),"Live caches/settings/journal changed during offline smoke.");
                File.WriteAllText(Path.Combine(folder,"result.txt"),"PASS: maximized shared builder; observed FIXED vs proposed AUTO copies; proposal click cycle AUTO → FIXED → removed; one-toggle proposal removal; undo state restoration; dirty draft protection; review header/name/card persistence; raw evidence preservation; ordinary-mode isolation; settings defaults/round trip; live Stop availability; offline/reentrant/duplicate stop guards and drain-before-save order. Rendered wide/compact review and settings; live file hashes unchanged; no capture or network.");
            return 0;
        }
        catch(Exception error) { File.WriteAllText(Path.Combine(folder,"result.txt"),"FAIL: "+error); return 1; }
        finally
        {
            if(editor is not null) { typeof(DeckBuilderWindow).GetField("_dirty",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(editor,false); editor.Close(); }
            if(window is not null) { window._visionWorker=null; window._autoEncounterSave=null; window._builderWindow=null; window.Close(); }
        }
    }
}
