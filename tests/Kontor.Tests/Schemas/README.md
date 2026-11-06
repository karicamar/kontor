# ISO 20022 schemas

`pain.001.001.09.xsd` and `camt.053.001.08.xsd` are the official message schemas, downloaded unchanged from
[iso20022.org](https://www.iso20022.org/iso-20022-message-definitions) (Payments Initiation and Cash Management).
The tests validate every file Kontor writes against them, so a wrong element order or a missing mandatory element fails the build.
