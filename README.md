# OLX watcher

Two AWS Lambda projects targeting .NET 8 are deployed from one Serverless Framework configuration:

- `OlxWatcher.ListingsApi` — Telegram webhook endpoint. Supports `/watch <OLX URL>` and `/list`.
- `OlxWatcher.ListingsWatcher` — scheduled listing check, every 15 minutes.

## Deploy

Install the Node and .NET dependencies, then deploy both functions together:

```bash
npm install
dotnet tool install -g Amazon.Lambda.Tools
dotnet restore
npm run deploy -- --stage dev --region eu-central-1

##
serverless package
serverless deploy --stage dev --region eu-central-1
```

Use `npm run package` (or `serverless package`) to build deployment artifacts without deploying. The Bash script at `scripts/package-dotnet.sh` packages each .NET project into its configured, uniquely named ZIP artifact, allowing one service to deploy both projects. AWS credentials must be configured for the target account.

## Telegram setup

Create the following `SecureString` parameters in the same AWS Region and stage that you deploy to:

```bash
aws ssm put-parameter \
  --name /olx-watcher/dev/telegram-bot-token \
  --type SecureString \
  --value '<Telegram bot token>'

aws ssm put-parameter \
  --name /olx-watcher/dev/telegram-webhook-secret \
  --type SecureString \
  --value '<random webhook secret>'
```

Replace `dev` with the deployment stage. Use `--overwrite` when rotating a value. Serverless resolves these parameters during deployment, so the AWS profile used for deployment needs permission to read and decrypt them. After deployment, register the returned HTTP API endpoint plus `/telegram/webhook` as the bot webhook and supply the same secret token. The deployment creates a pay-per-request DynamoDB table named `olx-watcher-<stage>-watched-products`.
