/**
 * Karma configuration.
 *
 * Exists for one reason: **karma's default port is 9876, and so is Blender's MCP add-on.**
 *
 * When both are on the machine, karma reports its server as started, launches the browser at
 * http://localhost:9876/, and the browser reaches a raw TCP socket speaking a protocol that is not
 * HTTP. Nothing captures, and two minutes later karma gives up with "ChromeHeadless has not captured
 * in 60000 ms" — which reads as a browser problem and is nothing of the sort. 9876 is a popular
 * default; Blender is simply the one that got here first on this machine.
 *
 * Everything else is Angular's generated default.
 */
module.exports = function (config) {
  config.set({
    basePath: '',
    frameworks: ['jasmine', '@angular-devkit/build-angular'],
    plugins: [
      require('karma-jasmine'),
      require('karma-chrome-launcher'),
      require('karma-jasmine-html-reporter'),
      require('karma-coverage'),
      require('@angular-devkit/build-angular/plugins/karma'),
    ],
    client: {
      jasmine: {},
      clearContext: false,
    },
    jasmineHtmlReporter: { suppressAll: true },
    coverageReporter: {
      dir: require('path').join(__dirname, './coverage'),
      subdir: '.',
      reporters: [{ type: 'html' }, { type: 'text-summary' }],
    },
    reporters: ['progress', 'kjhtml'],
    browsers: ['Chrome'],
    restartOnFileChange: true,

    // Not 9876. See the note above.
    port: 9899,
  });
};
