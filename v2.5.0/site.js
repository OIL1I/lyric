// The version switcher, and nothing else.
//
// The list is fetched rather than baked into every page: a version written today cannot know which
// versions will exist tomorrow, and a frozen release must not need rewriting to learn about them.
//
// When the fetch fails — opened from the file system, or no index yet — the switcher stays empty.
// The page itself works without it.

(function () {
    var host = document.querySelector('.switcher');
    if (!host) return;

    var source = host.getAttribute('data-versions');
    if (!source) return;

    // '/v1.0.0/guide/functions/' -> 'v1.0.0'. Taken from the location rather than from a variable
    // so a directory renamed by hand still selects the right entry.
    var segments = window.location.pathname.split('/').filter(Boolean);
    var current = null;
    for (var i = 0; i < segments.length; i++) {
        if (segments[i] === 'nightly' || /^v\d+\.\d+\.\d+$/.test(segments[i])) {
            current = segments[i];
            break;
        }
    }

    fetch(source)
        .then(function (response) {
            if (!response.ok) throw new Error(response.status);
            return response.json();
        })
        .then(function (versions) {
            if (!Array.isArray(versions) || versions.length < 2) return;

            var select = document.createElement('select');
            select.setAttribute('aria-label', 'Documentation version');

            versions.forEach(function (entry) {
                var option = document.createElement('option');
                option.value = entry.version;
                option.textContent = entry.stable ? entry.version : entry.version + ' (unstable)';
                if (entry.version === current) option.selected = true;
                select.appendChild(option);
            });

            select.addEventListener('change', function () {
                // To the same page in the other version when it exists there, otherwise its root.
                // The page cannot know, so the browser finds out: a 404 lands on the root instead.
                var rest = segments.slice(segments.indexOf(current) + 1).join('/');
                var base = source.replace(/versions\.json$/, '');
                window.location.href = base + select.value + '/' + (rest ? rest + '/' : '');
            });

            host.appendChild(select);
        })
        .catch(function () {
            /* no switcher, no problem */
        });
})();
