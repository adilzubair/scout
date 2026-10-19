if (args.Length == 2 && args[0] == "--index-lifecycle") return LifecycleAudit.Run(args[1]);
return 1;
