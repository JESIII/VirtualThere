// SPDX-License-Identifier: Apache-2.0
//
// vht-client process entry point. Kept deliberately thin: everything testable lives in the
// libraries under this project, so the tests never need to spawn a process.

using Vht.Client.Cli;

return await ClientCli.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
