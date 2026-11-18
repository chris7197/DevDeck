// Run: node tests/Test-ProjectActionInspector.js
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'plugin/property-inspector.html'), 'utf8');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'plugin/manifest.json'), 'utf8'));
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]);
assert.equal(new Set(ids).size, ids.length);
class Element {
    constructor(hidden) {
        this.value = ''; this.checked = false; this.validity = { valid: true }; this.handlers = {};
        this.classList = { hidden, remove: () => this.classList.hidden = false,
            toggle: (_, value) => this.classList.hidden = value };
    }
    addEventListener(event, fn) { this.handlers[event] = fn; }
    change(value, checked = false) { this.value = value; this.checked = checked; this.handlers.input(); this.handlers.change(); }
}
function inspector(action, initial = {}) {
    const messages = [], elements = Object.fromEntries(ids.map(id => [id, new Element(html.includes(`id="${id}" class="hidden"`))]));
    let socket;
    class WebSocket {
        static OPEN = 1;
        constructor() { this.readyState = 1; socket = this; }
        send(text) { messages.push(JSON.parse(text)); }
    }
    const window = {};
    vm.runInNewContext(script, { window, WebSocket, document: { getElementById: id => elements[id],
        querySelectorAll: () => Object.values(elements) }, setTimeout, clearTimeout });
    window.connectElgatoStreamDeckSocket('12345', 'key-1', 'registerPropertyInspector', '{}',
        JSON.stringify({ action: 'com.chris7197.devdeck.' + action, payload: { settings: initial } }));
    socket.onopen();
    const last = () => messages.filter(m => m.event === 'setSettings').at(-1)?.payload;
    return { elements, last };
}
for (const id of ['openprojectfile', 'runproject']) {
    assert.equal(manifest.Actions.find(a => a.UUID === 'com.chris7197.devdeck.' + id).PropertyInspectorPath, 'property-inspector.html');
}
const file = inspector('openprojectfile', { UnknownSetting: 'keep' });
assert.equal(file.elements.projectfile.classList.hidden, false);
assert.equal(file.elements.runproject.classList.hidden, true);
assert.equal(file.elements['projectfile-custom'].classList.hidden, true);
file.elements.ProjectFileUseCustom.change('true');
assert.equal(file.elements['projectfile-custom'].classList.hidden, false);
file.elements.Label.change('EDMX');
file.elements.ProjectFilePathFilter.change('*BitMan*');
file.elements.ProjectFilePath.change('Data/MainDBEntities.edmx');
file.elements.ProjectFileEditor.change('XML (Text) Editor');
assert.equal(file.last().ProjectFileUseCustom, true);
assert.equal(file.last().ProjectFilePath, 'Data/MainDBEntities.edmx');
assert.equal(file.last().ProjectFileEditor, 'XML (Text) Editor');
assert.equal(file.last().UnknownSetting, 'keep');
assert.equal(file.last().RunProjectUseCustom, undefined);
const restored = inspector('openprojectfile', file.last());
assert.equal(restored.elements.ProjectFilePath.value, 'Data/MainDBEntities.edmx');
restored.elements.ProjectFileUseCustom.change('false');
assert.equal(restored.last().ProjectFileUseCustom, false);
assert.equal(restored.elements['projectfile-custom'].classList.hidden, true);
const run = inspector('runproject');
assert.equal(run.elements.runproject.classList.hidden, false);
assert.equal(run.elements.projectfile.classList.hidden, true);
run.elements.RunProjectUseCustom.change('true');
assert.equal(run.last().RunProjectConfirm, true);
assert.equal(run.last().RunProjectSaveAll, true);
assert.equal(run.last().RunProjectBuildMode, 'Rebuild');
run.elements.RunProjectPathFilter.change('*BitMan*');
run.elements.RunProjectPath.change('DbMigrator/DbMigrator.csproj');
run.elements.RunProjectConfirm.change('', false);
run.elements.RunProjectBuildMode.change('Build');
run.elements.RunProjectBuildTimeoutSeconds.change('90');
assert.equal(run.last().RunProjectConfirm, false);
assert.equal(run.last().RunProjectBuildTimeoutSeconds, 90);
assert.equal(run.last().RunProjectBuildMode, 'Build');
assert.equal(run.last().RunProjectPath, 'DbMigrator/DbMigrator.csproj');
assert.equal(file.last().Label, 'EDMX');
assert.equal(file.last().RunProjectPath, undefined);
const slot = inspector('slot01');
assert.equal(slot.elements.projectfile.classList.hidden, false);
assert.equal(slot.elements.runproject.classList.hidden, false);
slot.elements.RunProjectUseCustom.change('false');
assert.equal(slot.last().RunProjectUseCustom, false);
console.log('PASS: manifest registration, separate inspectors, per-key storage, reload, default safeguards and profile slots');
