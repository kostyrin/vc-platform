const core = require('@actions/core');
const github = require('@actions/github');
const fs = require('fs');
const xml2js = require('xml2js');
const parser = new xml2js.Parser();

String.isNullOrEmpty = function(value) {
    return !(typeof value === "string" && value.length > 0);
}

fs.readFile('Directory.Build.Props', function (err, data) {
    if (!err) {

        parser.parseString(data, function (err, json) {
            if (!err) {
                console.log(json);

                var prefix = json.Project.PropertyGroup[0].VersionPrefix.trim();
                var suffix = json.Project.PropertyGroup[0].VersionSuffix[0].trim();
        
                let version = prefix + (suffix != '' ? '-' + suffix : '') + github.sha.substring(0, 8)
        
                core.setOutput("tag", version);
        
                console.log(`Version tag is: ${version}`);
            }
        });
    }
});