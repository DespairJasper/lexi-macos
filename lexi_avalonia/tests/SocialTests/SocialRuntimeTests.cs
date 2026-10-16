using Lexi.Social;

internal static class SocialRuntimeTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"lexi-runtime-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            var actor=Guid.NewGuid();var binding=Guid.NewGuid();var now=DateTimeOffset.Parse("2026-10-11T04:00:00Z");var day=new DateOnly(2026,10,11);
            var exclusions=new Dictionary<string,IReadOnlySet<string>>{{"plan",new HashSet<string>{"local-word-id"}}};
            var store=new SocialStore(root);
            void Deny(Action action,string message)
            {
                try {action();check(false,message);}catch(InvalidOperationException){check(true,message);}
            }
            Deny(()=>store.PrepareBinding(actor,binding,"test",now,exclusions,false),"unauthorized binding never creates intent");
            check(store.GetBinding(actor) is null,"denied binding leaves no owner");
            var intent=store.PrepareBinding(actor,binding,"test",now,exclusions,true);
            store=new SocialStore(root);
            check(store.GetBinding(actor)==intent && intent.BoundAt is null,"binding intent persists before HTTP/crash");
            check(store.PrepareBinding(actor,Guid.NewGuid(),"changed",now.AddHours(1),new Dictionary<string,IReadOnlySet<string>>(),true)==intent,"repeated authorization cannot replace pending UUID or exclusions");
            Deny(()=>store.PrepareBinding(Guid.NewGuid(),Guid.NewGuid(),"other",now,exclusions,true),"B cannot claim A intent");
            check(store.GetBindingExclusions(actor,"plan",day).Contains("local-word-id") && store.GetBindingExclusions(actor,"plan",day.AddDays(1)).Count==0,"exclusions are persisted and apply only original binding day");
            var facts=new SocialDayObservation(day,[new("plan",true,1,1,now.AddMinutes(1))],1,0,60,now.AddMinutes(1),now.AddMinutes(1),QualificationCause.LearningComplete);
            Deny(()=>store.ObserveDay(actor,facts,true),"pending binding cannot upload observations");
            store.ActivateBinding(actor,binding,now,day);
            var closed=store.ObserveDay(actor,facts with{Cause=QualificationCause.PlanChange},true);
            check(closed.Summary.QualifiedAt is null,"unconfirmed first-day baseline fails closed even for plan changes");
            store.ConfirmBindingBaseline(actor,binding,now,exclusions);
            var first=store.ObserveDay(actor,facts,true);
            check(first.Summary.Revision==2 && first.Summary.QualifiedAt==now.AddMinutes(1) && first.Summary.TimeTrusted,"actual completion becomes first trusted projection");
            check(store.ObserveDay(actor,facts with{ObservedAt=now.AddMinutes(2)},true).Summary.Revision==2 && store.LoadPending(actor).Count==2,"polling unchanged projection creates no batch");
            var changed=store.ObserveDay(actor,facts with{ReviewWords=1,ObservedAt=now.AddMinutes(3),LastActualCompletionAt=now.AddMinutes(3),Cause=QualificationCause.Correction},false);
            check(changed.Summary.Revision==3 && changed.Summary.QualifiedAt==first.Summary.QualifiedAt && changed.Summary.Cause==QualificationCause.LearningComplete && changed.Summary.TimeTrusted,"still-qualified correction preserves original event timestamp/cause/trust");
            store=new SocialStore(root);
            check(store.LoadDay(actor,day)!.Facts.ReviewWords==1,"original facts survive restart");
            var recovered=store.ObserveDay(actor,changed.Facts,false,true);
            check(recovered.Summary.Cause==QualificationCause.Correction && !recovered.Summary.TimeTrusted && recovered.Summary.QualifiedAt==first.Summary.QualifiedAt,"startup recovery keeps real timestamp but cannot claim extreme trust");
            var revoked=store.ObserveDay(actor,facts with{Plans=[new("plan",true,2,1,now)],Cause=QualificationCause.PlanChange},true);
            check(revoked.Summary.QualifiedAt is null,"increasing target revokes qualified timestamp");
            var requalified=store.ObserveDay(actor,facts with{ObservedAt=now.AddMinutes(5),Cause=QualificationCause.PlanChange},true);
            check(requalified.Summary.Cause==QualificationCause.PlanChange && !requalified.Summary.TimeTrusted && requalified.Summary.QualifiedAt==now.AddMinutes(5),"plan-change requalification uses actual change time and never extreme");
            var zero=store.ObserveDay(actor,facts with{Plans=[new("plan",true,0,0,now)]},true);
            check(zero.Summary.QualifiedAt is null,"zero remaining tasks never qualify");
            Deny(()=>store.ObserveDay(actor,facts with{Date=day.AddDays(-1),LastActualCompletionAt=null},true),"binding cannot backfill past dates");
            Deny(()=>store.LoadDay(Guid.NewGuid(),day),"other account cannot read persisted original facts");
            check(store.QueueDuration(actor,new(day,60,"partial"),now),"duration change persists locally");
            check(!store.QueueDuration(actor,new(day,60,"partial"),now),"same duration does not create a revision");
            check(store.QueueDuration(actor,new(day,70,"partial"),now),"changed duration advances durable revision");
            check(store.LoadSecondaryPending(actor,now).Single().Revision==2,"latest duration replaces older unsent payload atomically");
            check(store.LoadSecondaryPending(actor,now.AddDays(8)).Count==0,"duration upload is capped at 8 dates");
            var points=Enumerable.Repeat(0.5,121).ToArray();
            check(store.QueueCurve(actor,new(now,10,points),now),"valid local model curve persists");
            check(!store.QueueCurve(actor,new(now,10,points),now),"same curve snapshot does not requeue");
            void RejectBounds(Action action,string message){try{action();check(false,message);}catch(ArgumentException){check(true,message);}}
            RejectBounds(()=>store.QueueDuration(actor,new(day,double.NaN,"complete"),now),"nonfinite duration cannot enter wire queue");
            RejectBounds(()=>store.QueueDuration(actor,new(day,null,"complete"),now),"missing duration requires unavailable coverage");
            RejectBounds(()=>store.QueueCurve(actor,new(now,10,new double[122]),now),"curve cannot exceed protocol point count");
            RejectBounds(()=>store.QueueCurve(actor,new(now,10,points.Select(x=>2.0).ToArray()),now),"curve predictions stay within probability range");
            check(store.LoadSecondaryPending(actor,now.AddDays(31)).Count==0,"expired curves cannot upload after 30 days");
            store.SetSecondarySharing(actor,false,false);
            check(store.GetSecondarySharing(actor)==new SocialSecondarySharing(false,false),"all-off sharing is durable across reopening");
            check(new SocialStore(root).LoadSecondaryPending(actor,now).Count==2,"sharing-off retains local secondary facts");
            check(store.LoadPending(actor).All(b=>!b.RawPayload.Contains("local-word-id")&&!b.RawPayload.Contains("\"plan\"")&&b.RawPayload.Contains("binding_id")),"wire summaries omit local plan and word identifiers");
            var crossing=new SocialStore(Path.Combine(root,"midnight"));var crossingOwner=Guid.NewGuid();var crossingId=Guid.NewGuid();
            var proposed=DateTimeOffset.Parse("2026-10-11T15:59:00Z");var actualBound=DateTimeOffset.Parse("2026-10-11T16:01:00Z");var nextDay=day.AddDays(1);
            crossing.PrepareBinding(crossingOwner,crossingId,"midnight",proposed,new Dictionary<string,IReadOnlySet<string>>{{"p",new HashSet<string>{"old-day-completion"}}},true);
            crossing.ActivateBinding(crossingOwner,crossingId,actualBound,nextDay);
            check(crossing.GetBindingExclusions(crossingOwner,"p",nextDay).Count==0,"old authorization-day exclusions do not migrate across Beijing midnight");
            var duringWait=new SocialDayObservation(nextDay,[new("p",true,1,1,actualBound.AddSeconds(-30))],0,0,0,actualBound.AddMinutes(1),actualBound.AddSeconds(-30),QualificationCause.PlanChange);
            check(crossing.ObserveDay(crossingOwner,duringWait,true).Summary.QualifiedAt is null,"cloud waiting interval cannot qualify before actual-bound baseline reconciliation");
            var actualBaseline=new Dictionary<string,IReadOnlySet<string>>{{"p",new HashSet<string>{"waiting-before-bound"}}};
            Deny(()=>crossing.ConfirmBindingBaseline(crossingOwner,crossingId,proposed,actualBaseline),"baseline cannot use proposed time in place of cloud BoundAt");
            crossing.ConfirmBindingBaseline(crossingOwner,crossingId,actualBound,actualBaseline);
            check(new SocialStore(Path.Combine(root,"midnight")).GetBinding(crossingOwner)!.FirstDayBaselineConfirmed,"actual-bound baseline confirmation survives restart");
            check(crossing.GetBindingExclusions(crossingOwner,"p",nextDay).SetEquals(new[]{"waiting-before-bound"}),"actual baseline includes pre-bound waiting completion, excludes old-day and after-bound completion");
            check(crossing.GetBindingExclusions(crossingOwner,"new-plan",nextDay).Count==0,"new plan created after binding defaults to empty exclusions");
            Deny(()=>crossing.ConfirmBindingBaseline(crossingOwner,crossingId,actualBound,new Dictionary<string,IReadOnlySet<string>>()),"confirmed first-day baseline cannot be overwritten later");
            crossing.ConfirmBindingBaseline(crossingOwner,crossingId,actualBound,actualBaseline);
            check(crossing.ObserveDay(crossingOwner,duringWait with{Plans=[new("p",true,0,0,null)]},true).Summary.QualifiedAt is null,"baseline leaves zero remaining work unqualified");
            check(crossing.ObserveDay(crossingOwner,duringWait with{Cause=QualificationCause.LearningComplete},true).Summary.QualifiedAt is null,"pre-cloud-bound actual completion remains ineligible after confirmation");
            var afterBound=duringWait with{ObservedAt=actualBound.AddMinutes(2),LastActualCompletionAt=actualBound.AddMinutes(2),Cause=QualificationCause.LearningComplete};
            check(crossing.ObserveDay(crossingOwner,afterBound,true).Summary.QualifiedAt==actualBound.AddMinutes(2),"completion after BoundAt while HTTP waited remains eligible after baseline confirmation");
            check(crossing.GetBindingExclusions(crossingOwner,"p",nextDay.AddDays(1)).Count==0,"confirmed baseline exclusions expire next Beijing day");
            var missed=new SocialStore(Path.Combine(root,"missed-completion"));var missedOwner=Guid.NewGuid();var missedBinding=Guid.NewGuid();
            missed.PrepareBinding(missedOwner,missedBinding,"recovery",now,new Dictionary<string,IReadOnlySet<string>>(),true);
            missed.ActivateBinding(missedOwner,missedBinding,now,day);
            missed.ConfirmBindingBaseline(missedOwner,missedBinding,now,new Dictionary<string,IReadOnlySet<string>>());
            var reconstructed=missed.ObserveDay(missedOwner,facts with{ObservedAt=now.AddDays(1),Cause=QualificationCause.Correction},false,true);
            check(reconstructed.Summary.QualifiedAt==facts.LastActualCompletionAt && reconstructed.Summary.Cause==QualificationCause.Correction && !reconstructed.Summary.TimeTrusted,"crash between original completion and sidecar save recovers prior-day actual timestamp without extreme");
            var backupDirectory=Path.Combine(root,"sidecar-backups");
            var backup=store.BackupIfChanged(backupDirectory,now);
            check(backup is not null && File.Exists(backup),"sidecar creates a consistent SQLite Backup API snapshot");
            using(var snapshot=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=backup!,Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly}.ToString()))
            {
                snapshot.Open();using var command=snapshot.CreateCommand();command.CommandText="PRAGMA integrity_check";
                check((string)command.ExecuteScalar()! == "ok","social backup has consistent SQLite pages");
                command.CommandText="SELECT count(*) FROM social_batches WHERE acknowledged=0";
                check(Convert.ToInt32(command.ExecuteScalar())==store.LoadPending(actor).Count,"backup includes durable pending queue");
            }
            check(store.BackupIfChanged(backupDirectory,now.AddMinutes(16)) is null,"unchanged sidecar is not backed up repeatedly");
            store.ObserveDay(actor,facts with{ReviewWords=20},false);
            check(store.BackupIfChanged(backupDirectory,now.AddMinutes(1)) is null,"changed sidecar still respects 15-minute throttle");
            check(store.BackupIfChanged(backupDirectory,now.AddMinutes(16)) is not null,"changed sidecar creates a later backup");
            store.ObserveDay(actor,facts with{ReviewWords=21},false);
            var concurrentBackups=await Task.WhenAll(Task.Run(()=>store.BackupIfChanged(backupDirectory,now.AddMinutes(32))),Task.Run(()=>store.BackupIfChanged(backupDirectory,now.AddMinutes(32))));
            check(concurrentBackups.Count(b=>b is not null)==1,"concurrent backup calls produce only one changed snapshot");
            store.ObserveDay(actor,facts with{ReviewWords=22},false);
            store.BackupIfChanged(backupDirectory,now.AddMinutes(48));
            check(Directory.GetFiles(backupDirectory,"*.sqlite3").Length==3,"social backup rotation retains at most three owned snapshots");
            var pendingBefore=store.LoadPending(actor).Count;
            store.PruneConfirmed(actor,day.AddDays(40));
            check(store.LoadPending(actor).Count==pendingBefore && store.LoadDay(actor,day) is not null,"retention preserves pending facts even beyond grace deadline");
            foreach(var pendingBatch in store.LoadPending(actor))store.Acknowledge(actor,pendingBatch.BatchId,pendingBatch.Hash);
            store.PruneConfirmed(actor,day.AddDays(40));
            check(store.LoadDay(actor,day) is null,"confirmed old facts are pruned after bounded retention");
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
        await Task.CompletedTask;
    }
}
