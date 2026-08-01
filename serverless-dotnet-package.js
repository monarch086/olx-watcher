'use strict';

const path = require('node:path');
const { execFileSync } = require('node:child_process');

class DotnetPackagePlugin {
  constructor(serverless) {
    this.serverless = serverless;
    this.hooks = {
      'before:package:initialize': this.packageDotnetFunctions.bind(this)
    };
  }

  packageDotnetFunctions() {
    const script = path.join(this.serverless.config.serviceDir, 'scripts', 'package-dotnet.sh');
    this.serverless.cli.log('Packaging .NET Lambda projects.');
    execFileSync('bash', [script], {
      cwd: this.serverless.config.serviceDir,
      stdio: 'inherit'
    });
  }
}

module.exports = DotnetPackagePlugin;
