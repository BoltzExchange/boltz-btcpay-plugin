const native = 'Native'
const ln = 'Lightning'
const chain = 'Chain'

function initApp({maxSend, lnInfo, chainInfo}) {
    return new Vue({
        el: '#WalletSend',
        data: {
            sendType: native,
            amount: null,
            sendAll: false,
        },
        computed: {
            limits() {
                switch (this.sendType) {
                    case ln:
                        return lnInfo.limits
                    case chain:
                        return chainInfo.limits
                    default:
                        return {minimal: 0, maximal: maxSend}
                }
            }
        },
        watch: {
            sendType: function (val) {
                this.amount = null
                this.sendAll = false
                this.destination = null;
            }
        }
    })

}


